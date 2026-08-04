using System.Net;
using Elsie.Web;

namespace Elsie.Soak.Soak;

/// <summary>
/// Wraps an in-process <see cref="ElsieApp"/> loopback server for one scenario. The ASP.NET-free
/// host runs on an ephemeral loopback port; HTTP/3 (when enabled) shares the same numeric port
/// on UDP.
/// </summary>
internal sealed class SoakServer : IAsyncDisposable
{
    private readonly ElsieTestServer _server;
    private readonly PhaseTimer _stopTimer = new();

    private SoakServer(ElsieTestServer server)
    {
        _server = server;
        Port = server.Endpoints[0].Port;
        Address = server.Endpoints[0].Address;
    }

    public int Port { get; }
    public IPAddress Address { get; }

    public static async Task<SoakServer> StartAsync(
        Action<ElsieListenOptions> configureListen,
        CancellationToken ct)
    {
        var app = ElsieApp.Create()
            .QuietConsole(false)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<SoakModule>()
            .Listen(IPAddress.Loopback, 0, configureListen);

        var server = await app.StartAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return new SoakServer(server);
    }

    /// <summary>Stops the server and reports how long the drain took (clean-stop assertion).</summary>
    public async Task<TimeSpan> StopAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        sw.Stop();
        return sw.Elapsed;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

/// <summary>The shared route set exercised by every scenario.</summary>
internal sealed class SoakModule : ElsieModule
{
    private static readonly byte[] BigBody = BuildBigBody();

    public SoakModule()
    {
        Get("/ping", () => ElsieResult.Text("pong"));
        Get("/slow", async (ctx, ct) =>
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            return ElsieResult.Text("slow");
        });
        Get("/big", () => ElsieResult.Bytes(BigBody, "application/octet-stream"));
        Post("/echo", async (ctx, ct) =>
        {
            var bytes = await ctx.Request.BufferBodyAsync(ct).ConfigureAwait(false);
            return ElsieResult.Bytes(bytes, "application/octet-stream");
        });
        Post("/upload", async (ctx, ct) =>
        {
            var bytes = await ctx.Request.BufferBodyAsync(ct).ConfigureAwait(false);
            return ElsieResult.Text(bytes.LongLength.ToString());
        });
    }

    private static byte[] BuildBigBody()
    {
        var bytes = new byte[1024 * 1024];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }
}