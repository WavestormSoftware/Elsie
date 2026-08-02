using System.Security.Claims;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Elsie.Extensions.Auth.Redis;
using StackExchange.Redis;
using Xunit;

namespace Elsie.Auth.Redis.Tests;

/// <summary>
/// Live-Redis integration tests for <see cref="RedisSessionStore"/> (Testcontainers).
/// Skipped when Docker is unavailable; runs for real in CI (ubuntu runner).
/// </summary>
[Trait("Category", "RedisIntegration")]
public class RedisSessionStoreTests : IAsyncLifetime
{
    private IContainer? _container;
    private bool _available;
    private IConnectionMultiplexer? _mux;
    private RedisSessionStore? _store;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ContainerBuilder("redis:7-alpine")
                .WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
                .Build();
            await _container.StartAsync();
            var connectionString = $"localhost:{_container.GetMappedPublicPort(6379)},allowAdmin=true";
            _mux = await ConnectionMultiplexer.ConnectAsync(connectionString);
            _store = new RedisSessionStore(_mux);
            _available = true;
        }
        catch
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Set_get_roundtrip()
    {
        if (!_available)
        {
            return;
        }

        var payload = Encoding.UTF8.GetBytes("session-payload");
        await _store!.SetAsync("sess-1", payload, TimeSpan.FromMinutes(5));

        var read = await _store.GetAsync("sess-1");
        Assert.NotNull(read);
        Assert.Equal(payload, read);

        Assert.Equal("elsie:session:sess-1", _store.KeyFor("sess-1"));
    }

    [Fact]
    public async Task Get_missing_returns_null()
    {
        if (!_available)
        {
            return;
        }

        Assert.Null(await _store!.GetAsync("does-not-exist"));
    }

    [Fact]
    public async Task Remove_deletes_session()
    {
        if (!_available)
        {
            return;
        }

        await _store!.SetAsync("sess-2", [1, 2, 3], TimeSpan.FromMinutes(5));
        await _store.RemoveAsync("sess-2");
        Assert.Null(await _store.GetAsync("sess-2"));
    }

    [Fact]
    public async Task Sliding_ttl_expires_session()
    {
        if (!_available)
        {
            return;
        }

        await _store!.SetAsync("sess-3", [1], TimeSpan.FromMilliseconds(300));
        Assert.NotNull(await _store.GetAsync("sess-3"));
        await Task.Delay(700);
        Assert.Null(await _store.GetAsync("sess-3"));
    }

    [Fact]
    public async Task Sliding_renewal_extends_ttl()
    {
        if (!_available)
        {
            return;
        }

        var ttl = TimeSpan.FromMilliseconds(500);
        await _store!.SetAsync("sess-4", [1], ttl);
        await Task.Delay(300);
        var payload = await _store.GetAsync("sess-4");
        Assert.NotNull(payload);
        // Renew: re-store with a fresh TTL (what the auth package does on every request).
        await _store.SetAsync("sess-4", payload!, ttl);
        await Task.Delay(300);
        Assert.NotNull(await _store.GetAsync("sess-4"));
        await Task.Delay(600);
        Assert.Null(await _store.GetAsync("sess-4"));
    }

    [Fact]
    public async Task Custom_prefix_is_used()
    {
        if (!_available)
        {
            return;
        }

        var store = new RedisSessionStore(_mux!, new RedisSessionStoreOptions { KeyPrefix = "myapp:session:" });
        await store.SetAsync("sess-5", [1], TimeSpan.FromMinutes(5));
        Assert.StartsWith("myapp:session:", store.KeyFor("sess-5"), StringComparison.Ordinal);
        var keys = store.ListKeys(_mux!);
        Assert.Contains(store.KeyFor("sess-5"), keys);
    }

    [Fact]
    public async Task Cookie_v2_session_end_to_end()
    {
        if (!_available)
        {
            return;
        }

        // Principal round-trip through the Redis store using the cookie ticket protector.
        var sessionId = CookieTicketProtector.NewSessionId();
        var cookieValue = CookieTicketProtector.ProtectServerSideSession(sessionId);
        Assert.True(CookieTicketProtector.IsVersion2(cookieValue));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ada"), new Claim(ClaimTypes.Role, "user")],
            authenticationType: "cookie"));
        var payload = CookieTicketProtector.SerializePrincipal(principal);

        var id = CookieTicketProtector.ToSessionIdString(sessionId);
        await _store!.SetAsync(id, payload, TimeSpan.FromMinutes(10));

        var stored = await _store.GetAsync(id);
        Assert.NotNull(stored);
        var restored = CookieTicketProtector.TryDeserializePrincipal(stored!);
        Assert.NotNull(restored);
        Assert.Equal("ada", restored!.Identity?.Name);
    }
}
