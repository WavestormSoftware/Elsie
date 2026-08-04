using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Regression: DisconnectWatcher used a blocking Peek-receive that raced the connection
/// handler's own body reads (Poll readable → handler drains → Receive blocks forever),
/// permanently wedging keep-alive connections under body-bearing traffic with the default
/// AbortRequestsOnClientDisconnect=true. This hammers one keep-alive connection with
/// POST bodies of varying sizes; a wedged watcher makes the loop stall within a few
/// hundred iterations, so an overall deadline turns the defect into a test failure.
/// </summary>
public class DisconnectWatcherRegressionTests
{
    private sealed class UploadModule : ElsieModule
    {
        public UploadModule()
        {
            Post("/upload", async (ctx, ct) =>
            {
                var body = await ctx.Request.ReadTextAsync(ct).ConfigureAwait(false);
                return ElsieResult.Text(body.Length.ToString(CultureInfo.InvariantCulture));
            });
        }
    }

    private sealed class AbortObservingModule : ElsieModule
    {
        public static readonly ConcurrentBag<long> Aborted = new();
        public static readonly ConcurrentBag<long> Completed = new();

        public AbortObservingModule()
        {
            Post("/observe", async (ctx, ct) =>
            {
                var id = Interlocked.Increment(ref _counter);
                try
                {
                    // Wait for the request to be aborted (or the body to finish). Use a
                    // non-cancellable delay so the loop keeps polling RequestAborted even
                    // after the dispatch token is cancelled by the abort.
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (DateTime.UtcNow < deadline && !ctx.RequestAborted.IsCancellationRequested)
                    {
                        await Task.Delay(20).ConfigureAwait(false);
                    }

                    if (ctx.RequestAborted.IsCancellationRequested)
                    {
                        Aborted.Add(id);
                    }
                    else
                    {
                        Completed.Add(id);
                    }

                    return ElsieResult.Text("done");
                }
                catch (Exception)
                {
                    return ElsieResult.Text("error");
                }
            });
        }

        private static long _counter;
    }

    [Fact]
    public async Task Keepalive_post_body_churn_does_not_wedge()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<UploadModule>()
            .StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Endpoints[0].Address, server.Endpoints[0].Port, cts.Token);
        await using var ns = tcp.GetStream();

        var sizes = new[] { 0, 512, 65536, 3, 262144, 128, 1048576, 64 };
        var readBuffer = new byte[8192];
        for (var i = 0; i < 400; i++)
        {
            var body = new byte[sizes[i % sizes.Length]];
            Array.Fill(body, (byte)'x');
            var head = Encoding.ASCII.GetBytes(
                $"POST /upload HTTP/1.1\r\nHost: localhost\r\nContent-Length: {body.Length}\r\n\r\n");
            await ns.WriteAsync(head, cts.Token);
            await ns.WriteAsync(body, cts.Token);

            // Read through the end of the headers, then exactly Content-Length body bytes.
            var header = new StringBuilder();
            var bodyStart = -1;
            var buffered = new MemoryStream();
            while (bodyStart < 0)
            {
                var n = await ns.ReadAsync(readBuffer, cts.Token);
                Assert.NotEqual(0, n);
                buffered.Write(readBuffer, 0, n);
                bodyStart = IndexOf(buffered.GetBuffer(), (int)buffered.Length, "\r\n\r\n"u8);
            }

            var bytes = buffered.ToArray();
            var headerText = Encoding.ASCII.GetString(bytes, 0, bodyStart);
            Assert.Contains(" 200 ", headerText, StringComparison.Ordinal);
            Assert.DoesNotContain("Transfer-Encoding", headerText, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A graceful FIN (half-close) that is followed by a RST (reset) must still fire
    /// <see cref="ElsieRequest.RequestAborted"/>. The watcher previously stopped at FIN, so a
    /// later RST was silently missed; the handler kept running to completion.
    /// Linux-only: Windows does not surface the FIN→RST transition through socket
    /// readability/error probing the way Linux does (the plain-RST abort path — without a
    /// prior FIN — is covered cross-platform by ParserAdversarialTests); on Windows a
    /// post-FIN reset unwinds the handler via the response-write failure instead.
    /// </summary>
    [Fact]
    public async Task Post_fin_rst_fires_request_aborted()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // see class comment above: FIN→RST is not observable via Poll/Peek on Windows
        }

        AbortObservingModule.Aborted.Clear();
        AbortObservingModule.Completed.Clear();

        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<AbortObservingModule>()
            .StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(server.Endpoints[0].Address, server.Endpoints[0].Port, cts.Token);
        await using var ns = tcp.GetStream();

        // Send a request whose handler runs until RequestAborted fires.
        await ns.WriteAsync(Encoding.ASCII.GetBytes(
            "POST /observe HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n"), cts.Token);

        // Give the handler a moment to start, then graceful FIN...
        await Task.Delay(100, cts.Token);
        tcp.Client.Shutdown(SocketShutdown.Send);

        // ...then force a RST (LingerOption 0 + close). The watcher must observe the RST and
        // cancel RequestAborted.
        await Task.Delay(100, cts.Token);
        tcp.Client.LingerState = new LingerOption(true, 0);
        tcp.Client.Close();

        // Wait for the handler to observe the abort (up to the deadline).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && AbortObservingModule.Aborted.IsEmpty && AbortObservingModule.Completed.IsEmpty)
        {
            await Task.Delay(50, cts.Token);
        }

        Assert.True(
            AbortObservingModule.Aborted.Count > 0,
            $"expected RequestAborted to fire after FIN+RST; completed={AbortObservingModule.Completed.Count} aborted={AbortObservingModule.Aborted.Count}");
    }

    private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}
