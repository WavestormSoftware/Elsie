using System.Net;
using Elsie.Auth;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Auth.Tests;

public class CookieHardeningTests
{
    [Fact]
    public async Task Secure_and_MaxAge_emitted_by_default()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "t" };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
            });
            s.AddElsieModule<TestAuthModule>();
        });

        var login = await host.PostJsonAsync("/login", new TestAuth.LoginBody("ada", "pass"));
        login.EnsureSuccessStatusCode();
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("Secure", setCookie);
        // Default MaxAge is 8 hours → Max-Age=28800.
        Assert.Contains("Max-Age=28800", setCookie);
        Assert.Contains("HttpOnly", setCookie);
    }

    [Fact]
    public void Host_prefix_requires_secure_path_and_no_domain()
    {
        // Name not matching the required prefix → throw.
        var s1 = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => s1.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions { CookieName = "t", CookiePrefix = "__Host-" };
            o.Cookie.TicketKeyFromString("test-ticket-key!!");
        }));

        // __Host- with Secure off → throw.
        var s2 = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => s2.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions { CookieName = "__Host-t", CookiePrefix = "__Host-", Secure = false };
            o.Cookie.TicketKeyFromString("test-ticket-key!!");
        }));

        // __Host- with a Domain → throw.
        var s3 = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => s3.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions
            {
                CookieName = "__Host-t",
                CookiePrefix = "__Host-",
                CookieDomain = "example.com"
            };
            o.Cookie.TicketKeyFromString("test-ticket-key!!");
        }));

        // Valid __Host- combination passes.
        var s4 = new ServiceCollection();
        s4.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions { CookieName = "__Host-t", CookiePrefix = "__Host-" };
            o.Cookie.TicketKeyFromString("test-ticket-key!!");
        });
    }

    [Fact]
    public async Task Host_prefix_cookie_emits_secure_and_path()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions { CookieName = "__Host-t", CookiePrefix = "__Host-" };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<TestSecureModule>();
        });

        var login = await host.PostJsonAsync("/login", new TestAuth.LoginBody("ada", "pass"));
        login.EnsureSuccessStatusCode();
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("__Host-t=", setCookie);
        Assert.Contains("Secure", setCookie);
        Assert.Contains("Path=/", setCookie);
    }

    [Fact]
    public void AddElsieAuth_requires_ticket_key_without_development_key()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddElsieAuth(o =>
        {
            o.Cookie = new ElsieCookieAuthOptions { AllowInsecureDevelopmentKey = false };
        }));
    }
}
