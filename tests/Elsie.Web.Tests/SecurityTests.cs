using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Elsie.Auth;
using Elsie.Testing;
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

            Get("/secret", ctx =>
            {
                var user = ElsiePrincipal.GetUser(ctx);
                return user.Identity?.IsAuthenticated == true
                    ? ElsieResult.Text(user.Identity.Name ?? "ok")
                    : ElsieResult.Unauthorized();
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
    public async Task Static_files_reject_path_traversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "elsie-sec-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "ok.txt"), "safe");
        var secretDir = Path.GetFullPath(Path.Combine(root, ".."));
        // place a file next to root to try to escape into
        var outside = Path.Combine(secretDir, "outside-" + Guid.NewGuid().ToString("n") + ".txt");
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

            using var evil = await client.GetAsync("/files/../" + Path.GetFileName(outside));
            // either 400 invalid path or 404 not found — never the outside file
            Assert.NotEqual(HttpStatusCode.OK, evil.StatusCode);
            var text = await evil.Content.ReadAsStringAsync();
            Assert.DoesNotContain("leak", text, StringComparison.Ordinal);
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
        Assert.Contains("http", json, StringComparison.Ordinal); // cleartext listen
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

        // Tamper cookie value
        var cookies = host.Client.DefaultRequestHeaders.Contains("Cookie")
            ? null
            : host.Client; // HttpClientHandler stores cookies — overwrite via Cookie header on a new client

        using var handler = new HttpClientHandler { UseCookies = false };
        using var client = new HttpClient(handler) { BaseAddress = host.Client.BaseAddress };
        using var req = new HttpRequestMessage(HttpMethod.Get, "/secret");
        req.Headers.TryAddWithoutValidation("Cookie", "t=v1.AAAAAAAAAAAAAAAAAAAA_tampered");
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
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { AllowInsecureDevelopmentKey = false };
            }));
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
}
