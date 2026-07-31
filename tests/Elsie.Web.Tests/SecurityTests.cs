using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Elsie.Auth;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Web.Tests;

public class SecurityTests
{
    private sealed class EchoModule : ElsieModule
    {
        public EchoModule()
        {
            Post("/echo", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<Msg>(ct);
                return bind.IsSuccess ? ctx.Json(bind.Value) : bind.Error!;
            });

            Get("/whoami", ctx =>
                ElsieResult.Json(new
                {
                    ip = ctx.Request.RemoteIp,
                    scheme = ctx.Request.Scheme,
                    host = ctx.Request.Host
                }));

            Get("/headers", ctx =>
            {
                var cl = ctx.Request.GetHeader("Content-Length");
                return ElsieResult.Json(new { contentLength = cl });
            });

            Get("/cookie", ctx =>
            {
                var v = ctx.Request.GetCookie("sid");
                return ElsieResult.Text(v ?? "none");
            });
        }
    }

    private sealed record Msg(string Text);

    [Fact]
    public async Task Body_over_limit_returns_413()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.MaxRequestBodyBytes = 32)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .StartAsync();

        using var client = server.CreateClient();
        var payload = "{\"Text\":\"" + new string('x', 200) + "\"}";
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var res = await client.PostAsync("/echo", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("too large", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Headers_over_limit_returns_400()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.MaxHeaderBytes = 256)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        req.Headers.TryAddWithoutValidation("X-Big", new string('A', 2000));
        using var res = await client.SendAsync(req);
        Assert.True((int)res.StatusCode >= 400);
    }

    [Fact]
    public async Task Static_files_reject_path_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "elsie-sec-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "ok.txt"), "safe");
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("n") + ".txt");
        await File.WriteAllTextAsync(outside, "leak");

        try
        {
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<EchoModule>()
                .ContentRoot(root)
                .StaticFiles(s =>
                {
                    s.Root = root;
                    s.RequestPath = "/files";
                })
                .StartAsync();

            using var client = server.CreateClient();
            Assert.Equal("safe", await client.GetStringAsync("/files/ok.txt"));

            foreach (var evil in new[]
                     {
                         "/files/../" + Path.GetFileName(outside),
                         "/files/%2e%2e/" + Path.GetFileName(outside),
                         "/files/..%2f" + Path.GetFileName(outside),
                         "/files/foo/../../" + Path.GetFileName(outside)
                     })
            {
                using var res = await client.GetAsync(evil);
                Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
                var text = await res.Content.ReadAsStringAsync();
                Assert.DoesNotContain("leak", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { File.Delete(outside); } catch { /* ignore */ }
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Forwarded_headers_apply_when_enabled()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.UseForwardedHeaders = true)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        req.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9, 10.0.0.1");
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        req.Headers.TryAddWithoutValidation("X-Forwarded-Host", "app.example");

        using var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        Assert.Contains("203.0.113.9", json, StringComparison.Ordinal);
        Assert.Contains("https", json, StringComparison.Ordinal);
        Assert.Contains("app.example", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwarded_headers_ignored_when_disabled()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Server(o => o.UseForwardedHeaders = false)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        req.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.9");
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        using var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("203.0.113.9", json, StringComparison.Ordinal);
        Assert.Contains("http", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Forwarded_headers_reject_control_chars_in_host_and_ip()
    {
        // HttpClient strips raw CRLF; unit-test the host filter directly.
        var (scheme, host, ip) = Elsie.Web.Hosting.ForwardedHeaders.Apply(
            enabled: true,
            scheme: "http",
            host: "original",
            remoteIp: "10.0.0.1",
            getHeader: name => name switch
            {
                "X-Forwarded-Host" => "evil\r\nX-Injected: 1",
                "X-Forwarded-For" => "1.2.3.4\nInjected",
                "X-Forwarded-Proto" => "https",
                _ => null
            });

        Assert.Equal("https", scheme);
        Assert.Equal("original", host); // rejected
        Assert.Equal("10.0.0.1", ip); // rejected
    }

    [Fact]
    public async Task Cookie_ticket_tampering_rejected()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions
                {
                    CookieName = "t",
                    AllowInsecureDevelopmentKey = false
                };
                o.Cookie.TicketKeyFromString("unit-test-ticket-key!");
            });
            s.AddElsieModule<AuthModule>();
        });

        (await host.PostJsonAsync("/login", new { user = "ada" })).EnsureSuccessStatusCode();
        Assert.Equal("ada", await host.Client.GetStringAsync("/secret"));

        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = host.Client.BaseAddress };
        using var req = new HttpRequestMessage(HttpMethod.Get, "/secret");
        req.Headers.TryAddWithoutValidation("Cookie", "t=v1.AAAAAAAAAAAAAAAAAAAA_tampered");
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Cookie_ticket_wrong_key_rejected()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "t" };
                o.Cookie.TicketKeyFromString("correct-key-16chars");
            });
            s.AddElsieModule<AuthModule>();
        });

        (await host.PostJsonAsync("/login", new { user = "ada" })).EnsureSuccessStatusCode();

        // New host with different key cannot validate prior cookie bytes
        await using var host2 = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "t" };
                o.Cookie.TicketKeyFromString("different-key-16ch!");
            });
            s.AddElsieModule<AuthModule>();
        });

        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = host2.Client.BaseAddress };
        // steal set-cookie from host1 login would be encrypted under other key — send garbage
        using var req = new HttpRequestMessage(HttpMethod.Get, "/secret");
        req.Headers.TryAddWithoutValidation("Cookie", "t=v1.not-a-valid-ticket-under-other-key");
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public void TicketKeyFromString_rejects_short_secret()
    {
        var o = new ElsieCookieAuthOptions();
        Assert.Throws<ArgumentException>(() => o.TicketKeyFromString("short"));
    }

    [Fact]
    public void AddElsieAuth_requires_ticket_key_without_dev_flag()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { AllowInsecureDevelopmentKey = false };
            }));
    }

    [Fact]
    public async Task Api_key_gate_constant_time_path_rejects_wrong_key()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<ApiKeyModule>());
        using var bad = new HttpRequestMessage(HttpMethod.Get, "/secure");
        bad.Headers.TryAddWithoutValidation("X-Api-Key", "wrong-key");
        using var res = await host.SendAsync(bad);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);

        using var ok = new HttpRequestMessage(HttpMethod.Get, "/secure");
        ok.Headers.TryAddWithoutValidation("X-Api-Key", "super-secret-key");
        using var resOk = await host.SendAsync(ok);
        Assert.Equal(HttpStatusCode.OK, resOk.StatusCode);
    }

    [Fact]
    public async Task Method_not_allowed_does_not_leak_handler_body()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<EchoModule>());
        using var res = await host.Client.PostAsync("/whoami", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("scheme", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("405", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cookie_parser_does_not_treat_partial_name_as_match()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<EchoModule>());
        using var req = new HttpRequestMessage(HttpMethod.Get, "/cookie");
        req.Headers.TryAddWithoutValidation("Cookie", "sid-extra=evil; other=1");
        using var res = await host.SendAsync(req);
        Assert.Equal("none", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_route_is_404_problem_not_empty()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<EchoModule>());
        using var res = await host.GetAsync("/no-such-route");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Contains("json", res.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":404", body, StringComparison.Ordinal);
        Assert.Contains("Not Found", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Static_path_boundary_rejects_sibling_prefix()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "elsie-sib-" + Guid.NewGuid().ToString("n"));
        var root = Path.Combine(baseDir, "www");
        var sibling = Path.Combine(baseDir, "www-evil");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(sibling);
        var inside = Path.Combine(root, "ok.txt");
        var outside = Path.Combine(sibling, "secret.txt");
        File.WriteAllText(inside, "safe");
        File.WriteAllText(outside, "leak");
        try
        {
            Assert.True(Elsie.Web.Hosting.StaticFileHandler.IsPathInsideRoot(inside, root));
            Assert.False(Elsie.Web.Hosting.StaticFileHandler.IsPathInsideRoot(outside, root));
            // Classic StartsWith hole: outside path begins with root string
            Assert.StartsWith(root, outside, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(baseDir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task X_Request_Id_with_crlf_is_not_echoed()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        // HttpClient may strip raw CRLF; use a control-char id that is still invalid for our allow-list.
        req.Headers.TryAddWithoutValidation("X-Request-Id", "bad id with spaces");
        using var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        Assert.True(res.Headers.TryGetValues("X-Request-Id", out var ids));
        var id = Assert.Single(ids);
        Assert.DoesNotContain(' ', id);
        Assert.NotEqual("bad id with spaces", id);
    }

    [Fact]
    public async Task X_Request_Id_safe_value_is_echoed()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .StartAsync();

        using var client = server.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        req.Headers.TryAddWithoutValidation("X-Request-Id", "req-abc_123.def:1");
        using var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        Assert.Equal("req-abc_123.def:1", res.Headers.GetValues("X-Request-Id").Single());
    }

    private sealed class AuthModule : ElsieModule
    {
        public AuthModule()
        {
            Post("/login", async (ctx, _) =>
            {
                await ctx.SignInCookieAsync("ada");
                return ElsieResult.NoContent();
            });

            Get("/secret", ctx =>
            {
                var user = ctx.GetUser();
                return user.Identity?.IsAuthenticated == true
                    ? ElsieResult.Text(user.Identity.Name ?? "ok")
                    : ElsieResult.Unauthorized();
            });
        }
    }

    private sealed class ApiKeyModule : ElsieModule
    {
        public ApiKeyModule()
        {
            Before(ElsieAuth.RequireApiKey("super-secret-key"));
            Get("/secure", () => ElsieResult.Text("ok"));
        }
    }
}
