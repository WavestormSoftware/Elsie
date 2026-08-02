using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Elsie.Auth;
using Elsie.Testing;
using Xunit;

namespace Elsie.Auth.Tests;

public class ChallengeForbidTests
{
    /// <summary>
    /// A client that does not auto-follow redirects, so 302 challenge/forbid responses are observable.
    /// </summary>
    private static HttpClient NoRedirectClient(ElsieTestHost host)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
        return new HttpClient(handler) { BaseAddress = host.Client.BaseAddress };
    }

    [Fact]
    public async Task Cookie_challenge_redirects_to_login_path()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "t", Secure = false };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
                o.ChallengeLoginPath = "/login";
                o.ForbidAccessDeniedPath = "/denied";
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<TestSecureModule>();
            s.AddElsieModule<TestRoleModule>();
        });
        using var client = NoRedirectClient(host);

        var res = await client.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Cookie_challenge_falls_back_to_401_without_login_path()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "t", Secure = false };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<TestSecureModule>();
        });

        var res = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Forbid_redirects_to_access_denied_path_when_configured()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "t", Secure = false };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
                o.ForbidAccessDeniedPath = "/denied";
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<TestRoleModule>();
        });
        using var client = NoRedirectClient(host);

        // bob is authenticated but has no admin role → 302 to /denied.
        var login = await client.PostAsJsonAsync("/login", new TestAuth.LoginBody("bob", "pass"), ElsieJson.DefaultOptions);
        login.EnsureSuccessStatusCode();
        var res = await client.GetAsync("/roles/admin");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/denied", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Forbid_returns_403_without_access_denied_path()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "t", Secure = false };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<TestRoleModule>();
        });

        (await host.PostJsonAsync("/login", new TestAuth.LoginBody("bob", "pass"))).EnsureSuccessStatusCode();
        var res = await host.GetAsync("/roles/admin");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Jwt_challenge_returns_401_with_www_authenticate_bearer()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o => o.JwtBearer = new ElsieJwtBearerOptions
            {
                Authority = "https://idp.example",
                Audience = "api"
            });
            s.AddElsieModule<TestSecureModule>();
        });

        var res = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var www = Assert.Single(res.Headers.GetValues("WWW-Authenticate"));
        Assert.Equal("Bearer", www);
    }
}
