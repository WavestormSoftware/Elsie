using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Elsie.Web;
using Elsie.Auth;
using Elsie.Testing;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
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
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
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

    private sealed class PolicyModule : ElsieModule
    {
        public PolicyModule()
        {
            Path("/policy");
            Before(ElsieAuthGates.RequirePolicy("AdminsOnly"));
            Get("/", () => ElsieResult.Text("policy-ok"));
        }
    }

    private sealed record LoginBody(string User, string Password);

    private static ElsieTestHost CreateHost() =>
        ElsieTestHost.Create(
            services =>
            {
                services.AddElsieAuth(o =>
                {
                    o.Cookie = c =>
                    {
                        c.Cookie.Name = "elsie-auth";
                        c.Cookie.HttpOnly = true;
                        c.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;
                        c.SlidingExpiration = false;
                    };
                    o.Authorization = a =>
                    {
                        a.AddPolicy("AdminsOnly", p => p.RequireRole("admin"));
                    };
                });
                services.AddElsieModule<PublicModule>();
                services.AddElsieModule<SecureModule>();
                services.AddElsieModule<RoleModule>();
                services.AddElsieModule<ClaimModule>();
                services.AddElsieModule<PolicyModule>();
            },
            app =>
            {
                app.UseElsieAuth();
                app.MapElsie();
            });

    private static async Task<string> LoginCookieAsync(ElsieTestHost host)
    {
        var login = await host.PostJsonAsync("/login", new LoginBody("ada", "pass"));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var setCookie = login.Headers.GetValues("Set-Cookie").First();
        return setCookie.Split(';')[0];
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(ElsieTestHost host, string path, string cookie)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("Cookie", cookie);
        return await host.SendAsync(req);
    }

    [Fact]
    public async Task Anonymous_secure_is_401()
    {
        await using var host = CreateHost();
        var res = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Login_sets_cookie_and_unlocks_routes()
    {
        await using var host = CreateHost();
        var cookie = await LoginCookieAsync(host);
        Assert.StartsWith("elsie-auth=", cookie, StringComparison.Ordinal);

        using var me = await GetWithCookieAsync(host, "/me", cookie);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal("ada", doc.RootElement.GetProperty("name").GetString());

        using var secure = await GetWithCookieAsync(host, "/secure", cookie);
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.Equal("ok", await secure.Content.ReadAsStringAsync());

        using var admin = await GetWithCookieAsync(host, "/roles/admin", cookie);
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);

        using var claim = await GetWithCookieAsync(host, "/claims/named", cookie);
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        using var policy = await GetWithCookieAsync(host, "/policy", cookie);
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
    }

    [Fact]
    public async Task Role_gate_unauthorized_when_anonymous()
    {
        await using var host = CreateHost();
        var res = await host.GetAsync("/roles/admin");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Logout_issues_set_cookie()
    {
        await using var host = CreateHost();
        var cookie = await LoginCookieAsync(host);

        using var logoutReq = new HttpRequestMessage(HttpMethod.Post, "/logout");
        logoutReq.Headers.Add("Cookie", cookie);
        var logout = await host.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.True(logout.Headers.Contains("Set-Cookie"));
    }
}
