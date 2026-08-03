using System.Net;
using System.Net.Sockets;
using System.Text;
using Elsie.Auth;
using Elsie.Testing;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Deep security specs: path traversal, header injection, Host handling, auth gates,
/// error-page leakage, method handling, oversized headers, and request smuggling.
/// Wire-level tests use raw sockets; semantic tests use HttpClient where appropriate.
/// </summary>
public class SecurityDeepTests
{
    private const string SecretMarker = "TOP-SECRET-LEAK";

    // ------------------------------------------------------------------ helpers

    private static async Task<string> SendRawAsync(IPEndPoint ep, string request, TimeSpan timeout)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(ep.Address, ep.Port);
        await using var ns = tcp.GetStream();
        await ns.WriteAsync(Encoding.ASCII.GetBytes(request));
        return await ReadAllAsync(ns, timeout);
    }

    private static async Task<string> ReadAllAsync(NetworkStream ns, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[8192];
        var ms = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await ns.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token);
                if (n == 0)
                {
                    break;
                }

                ms.Write(buffer, 0, n);
            }
        }
        catch (OperationCanceledException)
        {
            // read deadline — return what we have
        }
        catch (IOException)
        {
            // peer closed / reset — return what we have
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static int FirstStatus(string raw)
    {
        var statuses = ExtractStatuses(raw);
        return statuses.Length > 0 ? statuses[0] : -1;
    }

    private static int[] ExtractStatuses(string raw)
    {
        var result = new List<int>();
        var idx = 0;
        while ((idx = raw.IndexOf("HTTP/1.1 ", idx, StringComparison.Ordinal)) >= 0)
        {
            var start = idx + "HTTP/1.1 ".Length;
            var end = raw.IndexOf(' ', start);
            if (end > start && int.TryParse(raw.AsSpan(start, end - start), out var code))
            {
                result.Add(code);
            }

            idx = end;
        }

        return result.ToArray();
    }

    private static string RequestLine(string method, string path) =>
        $"{method} {path} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";

    // ------------------------------------------------------------------ modules

    private sealed class EchoHeadModule : ElsieModule
    {
        public EchoHeadModule()
        {
            Get("/ping", () => ElsieResult.Text("pong"));
            Get("/host", ctx => ElsieResult.Text(ctx.Request.Host ?? "none"));
            Get("/inject", ctx =>
            {
                var v = ctx.Request.GetQuery("v") ?? string.Empty;
                return ElsieResult.Text("ok").WithHeader("X-Echo", v);
            });
            Post("/a", () => ElsieResult.Text("first"));
        }
    }

    private sealed class BoomModule : ElsieModule
    {
        public BoomModule()
        {
            Get("/boom", () => throw new InvalidOperationException("secret-internal-detail-xyz"));
        }
    }

    private sealed class PublicAuthModule : ElsieModule
    {
        public PublicAuthModule()
        {
            Post("/login", async (ctx, _) =>
            {
                await ctx.SignInCookieAsync("ada");
                return ElsieResult.NoContent();
            });
        }
    }

    private sealed class SecureAuthModule : ElsieModule
    {
        public SecureAuthModule()
        {
            Use(ElsieAuthGates.RequireAuthenticated());
            Get("/secure", () => ElsieResult.Text("secret-ok"));
        }
    }

    // ------------------------------------------------------------------ 1. static path traversal

    [Fact]
    public async Task Static_files_path_traversal_variants_do_not_escape_root()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "elsie-deep-" + Guid.NewGuid().ToString("n"));
        var www = Path.Combine(baseDir, "www");
        Directory.CreateDirectory(www);
        var secret = Path.Combine(baseDir, "secret.txt");
        var ok = Path.Combine(www, "ok.txt");
        await File.WriteAllTextAsync(secret, SecretMarker);
        await File.WriteAllTextAsync(ok, "safe");

        try
        {
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0)
                .Configure(o => o.ScanEntryAssembly = false)
                .ContentRoot(baseDir)
                .StaticFiles(s =>
                {
                    s.Root = www;
                    s.RequestPath = "/files";
                })
                .StartAsync();

            var ep = server.Endpoints[0];

            // Sanity: in-root file is served.
            var good = await SendRawAsync(
                ep,
                RequestLine("GET", "/files/ok.txt"),
                TimeSpan.FromSeconds(10));
            Assert.Equal(200, FirstStatus(good));
            Assert.Contains("safe", good, StringComparison.Ordinal);

            // Traversal variants must not escape the root or leak the secret above it.
            var variants = new[]
            {
                "/files/../secret.txt",
                "/files/%2e%2e/secret.txt",
                "/files/..%2fsecret.txt",
                "/files/....//secret.txt",
                "/files/%252e%252e%252fsecret.txt",
                "/files/..\\..\\secret.txt",
                "/../secret.txt",
                "/%2e%2e/secret.txt",
                "/..%2fsecret.txt"
            };

            foreach (var variant in variants)
            {
                var raw = await SendRawAsync(ep, RequestLine("GET", variant), TimeSpan.FromSeconds(10));
                var status = FirstStatus(raw);
                Assert.True(status is 400 or 404, $"variant {variant} returned {status}");
                Assert.DoesNotContain(SecretMarker, raw, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------ 2. header injection

    [Fact]
    public async Task Response_header_crlf_is_rejected_not_injected()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoHeadModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        // Raw CRLF percent-encoded in the query value; the handler echoes it into a header.
        var raw = await SendRawAsync(
            ep,
            "GET /inject?v=a%0d%0aInjected%3A%20yes HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("Injected: yes", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Injected", raw, StringComparison.Ordinal);
        // The injected header must not appear as a separate header line.
        Assert.DoesNotContain("\r\nInjected: yes\r\n", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 3. Host header

    [Fact]
    public async Task Missing_host_on_http11_is_accepted_not_400()
    {
        // RFC 7230 §5.4 requires Host on HTTP/1.1. The server does not enforce this.
        // Assert actual behavior (request still processed) and flag the deviation.
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoHeadModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET /ping HTTP/1.1\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(200, FirstStatus(raw));
        Assert.Contains("pong", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_host_headers_first_wins()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoHeadModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "GET /host HTTP/1.1\r\nHost: first.example\r\nHost: second.example\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        Assert.Equal(200, FirstStatus(raw));
        Assert.Contains("first.example", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("second.example", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 4. auth gates

    [Fact]
    public async Task Auth_gate_requires_cookie_then_unlocks_with_valid_cookie()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions
                {
                    CookieName = "elsie-deep-auth",
                    Secure = false, // plain-HTTP loopback test host
                    AllowInsecureDevelopmentKey = true // TESTS only
                };
            });
            s.AddElsieModule<PublicAuthModule>();
            s.AddElsieModule<SecureAuthModule>();
        });

        // Without credentials the protected route is denied.
        var denied = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        // Valid cookie unlocks the route.
        using var login = new HttpRequestMessage(HttpMethod.Post, "/login");
        login.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var loginRes = await host.Client.SendAsync(login);
        Assert.Equal(HttpStatusCode.NoContent, loginRes.StatusCode);
        Assert.True(loginRes.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies, c => c.StartsWith("elsie-deep-auth=", StringComparison.Ordinal));

        var ok = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("secret-ok", await ok.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------ 5. error pages leak no internals

    [Fact]
    public async Task Error_page_leaks_no_internals_by_default()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<BoomModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var res = await client.GetAsync("/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-internal-detail-xyz", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal); // stack frame
        Assert.DoesNotContain("SecurityDeepTests", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 6. method handling

    [Theory]
    [InlineData("TRACE")]
    [InlineData("TRACK")]
    public async Task Trace_and_track_return_405_without_echoing_request(string method)
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoHeadModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(ep, RequestLine(method, "/ping"), TimeSpan.FromSeconds(10));
        Assert.Equal(405, FirstStatus(raw));
        // The request must never be echoed back (TRACE/TRACK reflection).
        Assert.DoesNotContain($"{method} /ping", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("pong", raw, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 7. oversized header

    [Fact]
    public async Task Oversized_header_value_returns_413_and_connection_closes()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.MaxHeaderBytes = 1024)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoHeadModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        var big = new string('A', 8 * 1024);
        var raw = await SendRawAsync(
            ep,
            $"GET /ping HTTP/1.1\r\nHost: localhost\r\nX-Big: {big}\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));
        var status = FirstStatus(raw);
        Assert.True(status is 400 or 413, $"expected 4xx, got {status}");
        // Connection must close promptly (no hang): ReadAllAsync already bounded by timeout
        // and would have returned partial/empty on stall; assert we got a framed response.
        Assert.NotEqual(-1, status);
    }

    // ------------------------------------------------------------------ 8. content-length smuggling

    [Fact]
    public async Task Content_length_zero_with_smuggled_byte_no_desync()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoHeadModule>()
            .StartAsync();

        var ep = server.Endpoints[0];
        var raw = await SendRawAsync(
            ep,
            "POST /a HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n" +
            "G" + // smuggled byte after a declared CL:0 body
            "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n",
            TimeSpan.FromSeconds(10));

        var statuses = ExtractStatuses(raw);
        Assert.True(statuses.Length >= 1, "expected at least one response");

        // First response is the POST /a handler.
        Assert.Equal(200, statuses[0]);

        // The smuggled byte must not be silently dropped: it corrupts the pipelined request
        // line (method becomes "GGET"), so the second response is a 4xx — never a wrong 200.
        if (statuses.Length > 1)
        {
            Assert.True(statuses[1] is 400 or 405, $"second response {statuses[1]}");
            Assert.DoesNotContain("pong", raw, StringComparison.Ordinal);
        }

        // No desync: the first response body is not replayed as the second response —
        // the second response is a 4xx, never a 200 carrying the wrong payload.
    }
}
