using System.Net;
using Elsie.Auth;
using Elsie.Testing;
using Xunit;

namespace Elsie.Auth.Tests;

public class SessionStoreTests
{
    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Set_Get_Remove_roundtrip()
    {
        var store = new InMemoryElsieSessionStore();
        await store.SetAsync("s1", [1, 2, 3], TimeSpan.FromHours(1));
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.GetAsync("s1"));
        await store.RemoveAsync("s1");
        Assert.Null(await store.GetAsync("s1"));
    }

    [Fact]
    public async Task Expired_session_returns_null()
    {
        var time = new ManualTimeProvider();
        var store = new InMemoryElsieSessionStore(timeProvider: time);
        await store.SetAsync("s1", [1], TimeSpan.FromHours(1));

        time.Now = time.Now.AddHours(2);
        Assert.Null(await store.GetAsync("s1"));
    }

    [Fact]
    public async Task Read_renews_sliding_ttl()
    {
        var time = new ManualTimeProvider();
        var store = new InMemoryElsieSessionStore(timeProvider: time);
        await store.SetAsync("s1", [1], TimeSpan.FromHours(1));

        // A read shortly before expiry renews the session.
        time.Now = time.Now.AddMinutes(59);
        Assert.NotNull(await store.GetAsync("s1"));

        // Without the renewal this read would be past the original 1h TTL.
        time.Now = time.Now.AddMinutes(30);
        Assert.NotNull(await store.GetAsync("s1"));
    }

    [Fact]
    public async Task Store_is_bounded_and_evicts_lru()
    {
        var store = new InMemoryElsieSessionStore(maxEntries: 2);
        for (var i = 0; i < 10; i++)
        {
            await store.SetAsync($"s{i}", [(byte)i], TimeSpan.FromHours(1));
        }

        Assert.True(store.Count <= 2, $"expected eviction to cap entries, got {store.Count}");
    }

    [Fact]
    public async Task Session_login_restore_logout_lifecycle()
    {
        var store = new InMemoryElsieSessionStore();
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions
                {
                    CookieName = "elsie-auth",
                    Secure = false // plain-HTTP loopback test host
                };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
                o.SessionStore = store;
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<TestSecureModule>();
        });

        // Login emits an opaque v2 session id cookie and stores the principal server-side.
        var login = await host.PostJsonAsync("/login", new TestAuth.LoginBody("ada", "pass"));
        login.EnsureSuccessStatusCode();
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("elsie-auth=v2.", setCookie, StringComparison.Ordinal);
        Assert.Equal(1, store.Count);

        // Restore from the store.
        var me = await host.GetAsync("/me");
        me.EnsureSuccessStatusCode();
        Assert.Contains("ada", await me.Content.ReadAsStringAsync());

        // Sign-out removes the server-side entry and clears the cookie.
        (await host.Client.PostAsync("/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(0, store.Count);
        var res = await host.GetAsync("/secure");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Without_session_store_cookie_stays_client_side_v1()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsieAuth(o =>
            {
                o.Cookie = new ElsieCookieAuthOptions
                {
                    CookieName = "elsie-auth",
                    Secure = false
                };
                o.Cookie.TicketKeyFromString("test-ticket-key!!");
            });
            s.AddElsieModule<TestAuthModule>();
            s.AddElsieModule<TestSecureModule>();
        });

        var login = await host.PostJsonAsync("/login", new TestAuth.LoginBody("ada", "pass"));
        login.EnsureSuccessStatusCode();
        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("elsie-auth=v1.", setCookie, StringComparison.Ordinal);
        Assert.Equal("ok", await host.Client.GetStringAsync("/secure"));
    }
}
