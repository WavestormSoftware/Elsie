using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Elsie.Auth;
using Elsie.Testing;
using Xunit;

namespace Elsie.Auth.Tests;

public class AuthPackageTests
{
    private sealed class PublicModule : ElsieModule
    {
        public PublicModule()
        {
            Post("/login", async (ctx, ct) =>
            {
                var body = await ctx.BindJsonAsync<LoginBody>(ct);
                if (!body.IsSuccess || body.Value!.User != "ada" || body.Value.Password != "pass")
                {
                    return ElsieResult.Unauthorized("bad credentials");
                }

                await ctx.SignInCookieAsync(body.Value.User, roles: ["admin"]);
                return ElsieResult.NoContent();
            });

            Post("/logout", async (ctx, _) =>
            {
                await ctx.SignOutAsync();
                return ElsieResult.NoContent();
            });
        }
    }

    private sealed class SecureModule : ElsieModule
    {
        public SecureModule()
        {
            Before(ElsieAuthGates.RequireAuthenticated());

            Get("/me", ctx =>
            {
                var user = ctx.GetUser();
                return ctx.Json(new
                {
                    name = user.Identity!.Name,
                    roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray()
                });
            });

            Get("/secure", () => ElsieResult.Text("ok"));
        }
    }

    private sealed class RoleModule : ElsieModule
    {
        public RoleModule()
        {
            Path("/roles");
            Before(ElsieAuthGates.RequireRole("admin"));
            Get("/admin", () => ElsieResult.Text("admin-ok"));
        }
    }

    private sealed class ClaimModule : ElsieModule
    {
        public ClaimModule()
        {
            Path("/claims");
            Before(ElsieAuthGates.RequireClaim(ClaimTypes.Name, "ada"));
            Get("/named", () => ElsieResult.Text("named-ok"));
        }
    }

    private sealed record LoginBody(string User, string Password);

    private static ElsieTestHost CreateHost() =>
        ElsieTestHost.Create(services =>
        {
            services.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions
                {
                    CookieName = "elsie-auth",
                    HttpOnly = true,
                    SlidingExpiration = false,
                    Secure = false // plain-HTTP loopback test host
                };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
            });
            services.AddElsieModule<PublicModule>();
            services.AddElsieModule<SecureModule>();
            services.AddElsieModule<RoleModule>();
            services.AddElsieModule<ClaimModule>();
        });

    [Fact]
    public async Task Secure_requires_auth()
    {
        await using var host = CreateHost();
        var res = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_cookie_unlocks_secure()
    {
        await using var host = CreateHost();
        var login = await host.PostJsonAsync("/login", new LoginBody("ada", "pass"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies, c => c.StartsWith("elsie-auth=", StringComparison.Ordinal));

        // HttpClient handler stores cookies by default when using same client
        var me = await host.GetAsync("/me");
        me.EnsureSuccessStatusCode();
        var json = await me.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ada", doc.RootElement.GetProperty("name").GetString());

        Assert.Equal("ok", await host.Client.GetStringAsync("/secure"));
        Assert.Equal("admin-ok", await host.Client.GetStringAsync("/roles/admin"));
        Assert.Equal("named-ok", await host.Client.GetStringAsync("/claims/named"));
    }

    [Fact]
    public async Task Logout_clears_session()
    {
        await using var host = CreateHost();
        (await host.PostJsonAsync("/login", new LoginBody("ada", "pass"))).EnsureSuccessStatusCode();
        (await host.Client.PostAsync("/logout", null)).EnsureSuccessStatusCode();
        var res = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
