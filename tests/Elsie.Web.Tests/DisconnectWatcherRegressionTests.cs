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
