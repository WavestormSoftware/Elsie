using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Deep HTTP/2 behavior tests against a real TLS <see cref="ElsieApp"/> configured for
/// HTTP/2 only (ALPN h2). Exercises multiplexing, large bodies, trailers, cookie header
/// handling, concurrent stream isolation, and client-abort recovery over a single
/// request pipeline established by <see cref="System.Net.Http.HttpClient"/>.
/// </summary>
public class Http2DeepTests
{
    private sealed class DeepModule : ElsieModule
    {
        public DeepModule()
        {
            Get("/slow", async (ctx, ct) =>
            {
                await Task.Delay(100, ct);
                return ElsieResult.Text("slow");
            });

            Get("/slowlong", async (ctx, ct) =>
            {
                await Task.Delay(1500, ct);
                return ElsieResult.Text("slowlong");
            });

            Get("/fast", () => ElsieResult.Text("fast"));
            Get("/ping", () => ElsieResult.Text("pong"));

            Get("/big", () =>
            {
                var bytes = new byte[1024 * 1024];
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)(i % 251);
                }

                return ElsieResult.Bytes(bytes, "application/octet-stream");
            });

            Post("/echo", async (ctx, ct) =>
            {
                var bytes = await ctx.Request.BufferBodyAsync(ct);
                return ElsieResult.Bytes(bytes, "application/octet-stream");
            });

            Get("/trailers", ctx =>
            {
                ctx.Response.AddTrailer("grpc-status", "0");
                ctx.Response.AddTrailer("grpc-message", "done");
                return ElsieResult.Text("payload");
            });

            // Echo the Cookie header exactly as the server sees it (all values joined with "; ").
            Get("/cookie", ctx =>
                ElsieResult.Text(string.Join("; ", ctx.Request.GetHeaderValues("Cookie"))));
        }
    }

    [Fact]
    public async Task Multiplexing_ten_concurrent_requests_on_one_connection()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var sw = Stopwatch.StartNew();
        var tasks = new Task<(string Path, string Body)>[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            var path = i % 2 == 0 ? "/slow" : "/fast";
            tasks[i] = GetAsync(client, path, cts.Token);
        }

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        for (var i = 0; i < results.Length; i++)
        {
            var expected = i % 2 == 0 ? "slow" : "fast";
            Assert.Equal(expected, results[i].Body);
        }

        // 5 x 100 ms delays serialized would be >= 500 ms; multiplexed completes in ~100 ms.
        // SERVER-BUG: Http2Connection dispatches non-gRPC requests inline on the connection
        // loop (OnHeadersAsync -> MaybeDispatchAsync -> DispatchStreamAsync is awaited), so
        // concurrent streams are processed serially instead of in parallel. The gRPC path
        // uses Task.Run, but the regular path does not. Asserting the correct multiplexed
        // upper bound (well under 500 ms) is intentionally RED.
        Assert.True(
            sw.Elapsed < TimeSpan.FromMilliseconds(450),
            $"expected multiplexed completion, took {sw.Elapsed}");
    }

    [Fact]
    public async Task Large_response_body_1mb_streams_byte_for_byte()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var res = await client.GetAsync("/big", cts.Token);
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);

        // SERVER-BUG: Http2Connection.WriteResponseAsync writes DATA frames without respect
        // for the client's advertised receive window (state.SendWindow is tracked but never
        // used to throttle). Sending more than the peer's window is a FLOW_CONTROL_ERROR
        // (HTTP/2 RFC 9113 §6.9). The client detects it and aborts the read. Asserting correct
        // byte-for-byte delivery is intentionally RED.
        var body = await res.Content.ReadAsByteArrayAsync(cts.Token);
        Assert.Equal(1024 * 1024, body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            Assert.Equal((byte)(i % 251), body[i]);
        }
    }

    [Fact]
    public async Task Large_request_body_256kb_echoes_byte_for_byte()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var payload = new byte[256 * 1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        using var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var res = await client.PostAsync("/echo", content, cts.Token);
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);

        // SERVER-BUG: same send-side flow-control defect as Large_response_body_1mb — the
        // 256 KiB response is written without throttling to the client's receive window and
        // triggers FLOW_CONTROL_ERROR. Asserting correct byte-for-byte echo is RED.
        var echoed = await res.Content.ReadAsByteArrayAsync(cts.Token);
        Assert.Equal(payload, echoed);
    }

    [Fact]
    public async Task Grpc_style_trailers_are_delivered()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var res = await client.GetAsync("/trailers", cts.Token);
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);
        Assert.Equal("payload", await res.Content.ReadAsStringAsync(cts.Token));

        Assert.True(res.TrailingHeaders.TryGetValues("grpc-status", out var status));
        Assert.Equal("0", Assert.Single(status));
        Assert.True(res.TrailingHeaders.TryGetValues("grpc-message", out var message));
        Assert.Equal("done", Assert.Single(message));
    }

    [Fact]
    public async Task Nonexistent_route_returns_problem_json_404()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var res = await client.GetAsync("/no-such-route", cts.Token);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(HttpVersion.Version20, res.Version);
        Assert.Contains("problem+json", res.Content.Headers.ContentType?.MediaType ?? "");

        var body = await res.Content.ReadAsStringAsync(cts.Token);
        Assert.Contains("Not Found", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_cookie_headers_are_joined_and_echoed()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(client.BaseAddress!, "/cookie"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.TryAddWithoutValidation("Cookie", "a=1");
        request.Headers.TryAddWithoutValidation("Cookie", "b=2");

        using var res = await client.SendAsync(request, cts.Token);
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);
        Assert.Equal("a=1; b=2", await res.Content.ReadAsStringAsync(cts.Token));
    }

    [Fact]
    public async Task Concurrent_post_bodies_do_not_cross_contaminate()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var tasks = new List<Task<byte[]>>();
        for (var k = 0; k < 5; k++)
        {
            var body = new byte[32 * 1024];
            for (var i = 0; i < body.Length; i++)
            {
                body[i] = (byte)((i + k * 7) % 251);
            }

            tasks.Add(EchoAsync(client, body, cts.Token));
        }

        var echoes = await Task.WhenAll(tasks);
        for (var k = 0; k < 5; k++)
        {
            var expected = new byte[32 * 1024];
            for (var i = 0; i < expected.Length; i++)
            {
                expected[i] = (byte)((i + k * 7) % 251);
            }

            Assert.Equal(expected, echoes[k]);
        }
    }

    [Fact]
    public async Task Client_abort_mid_response_then_new_request_succeeds()
    {
        using var cert = CreateSelfSigned();
        await using var server = await StartServerAsync(cert);
        using var client = CreateClient(server.Endpoints[0].Port);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var abortCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        abortCts.CancelAfter(200);

        var slowTask = client.GetAsync("/slowlong", abortCts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slowTask);

        // A new request on the same client must still succeed and must not hang.
        using var res = await client.GetAsync("/ping", cts.Token);
        res.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, res.Version);
        Assert.Equal("pong", await res.Content.ReadAsStringAsync(cts.Token));
    }

    private static async Task<ElsieTestServer> StartServerAsync(X509Certificate2 cert) =>
        await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0, o =>
            {
                o.UseHttps = true;
                o.Certificate = cert;
                o.Protocols = ElsieHttpProtocols.Http2;
            })
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<DeepModule>()
            .StartAsync();

    private static HttpClient CreateClient(int port)
    {
        var ssl = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = static (_, _, _, _) => true
        };
        ssl.ApplicationProtocols = new List<SslApplicationProtocol>
        {
            SslApplicationProtocol.Http2,
            SslApplicationProtocol.Http11
        };
        var handler = new SocketsHttpHandler { SslOptions = ssl };
        return new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}/"),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }

    private static async Task<(string Path, string Body)> GetAsync(HttpClient client, string path, CancellationToken ct)
    {
        using var res = await client.GetAsync(path, ct);
        res.EnsureSuccessStatusCode();
        return (path, await res.Content.ReadAsStringAsync(ct));
    }

    private static async Task<byte[]> EchoAsync(HttpClient client, byte[] body, CancellationToken ct)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var res = await client.PostAsync("/echo", content, ct);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), password: null);
    }
}
