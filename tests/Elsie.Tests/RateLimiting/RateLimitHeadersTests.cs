using Elsie.RateLimiting;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests.RateLimiting;

public class RateLimitHeadersTests
{
    private sealed class HeaderModule : ElsieModule
    {
        public HeaderModule(IRateLimitStore store)
        {
            Use(ctx =>
            {
                var key = ctx.Request.RemoteIp ?? "unknown";
                return store.TryAcquire(key, out var retryAfter)
                    ? null
                    : ElsieResult.Problem(429, "Too Many Requests", "limited");
            });
            Use(ElsieRateLimitHeaders.Attach(store));
            Get("/ping", () => ElsieResult.Text("pong"));
        }
    }

    [Fact]
    public async Task Headers_are_emitted_for_memory_stores()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            var store = new InMemoryStore(permitLimit: 3);
            s.AddSingleton<IRateLimitStore>(store);
            s.AddElsieModule<HeaderModule>();
        });

        var response = await host.GetAsync("/ping");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("3", response.Headers.GetValues("X-RateLimit-Limit").Single());
        Assert.Equal("2", response.Headers.GetValues("X-RateLimit-Remaining").Single());
        var reset = response.Headers.GetValues("X-RateLimit-Reset").Single();
        Assert.True(long.TryParse(reset, out var resetUnix));
        Assert.True(resetUnix > 0, "Reset must be a future unix timestamp");
    }

    [Fact]
    public async Task Headers_are_omitted_when_store_does_not_support_peek()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            s.AddSingleton<IRateLimitStore>(new NoPeekStore());
            s.AddElsieModule<HeaderModule>();
        });

        var response = await host.GetAsync("/ping");
        Assert.Equal(200, response.StatusCode);
        Assert.False(response.Headers.Contains("X-RateLimit-Limit"));
        Assert.False(response.Headers.Contains("X-RateLimit-Remaining"));
        Assert.False(response.Headers.Contains("X-RateLimit-Reset"));
    }

    private sealed class InMemoryStore : IRateLimitStore
    {
        private int _count;

        public InMemoryStore(int permitLimit) => Limit = permitLimit;

        public int Limit { get; }

        public bool TryAcquire(string key, out TimeSpan retryAfter)
        {
            if (Interlocked.Increment(ref _count) > Limit)
            {
                retryAfter = TimeSpan.FromSeconds(10);
                return false;
            }

            retryAfter = TimeSpan.Zero;
            return true;
        }

        public bool TryPeek(string key, out RateLimitCounters counters)
        {
            counters = new RateLimitCounters(Limit, Math.Max(0, Limit - _count), DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds());
            return true;
        }
    }

    private sealed class NoPeekStore : IRateLimitStore
    {
        public bool TryAcquire(string key, out TimeSpan retryAfter)
        {
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}
