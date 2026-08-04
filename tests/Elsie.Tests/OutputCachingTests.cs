using System.Text;
using Elsie.Middleware;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

/// <summary>
/// Core tests for the <see cref="OutputCachingMiddleware"/>: a cached response is served without
/// re-running the handler, Accept-Encoding variants are keyed independently, no-store requests and
/// responses are skipped, conditional requests with a cached ETag produce 304, and the LRU evicts
/// at the configured cap.
/// </summary>
public class OutputCachingTests
{
    private sealed class CacheModule : ElsieModule
    {
        public static int HitCount;

        public CacheModule()
        {
            Get("/value", ctx =>
            {
                var n = Interlocked.Increment(ref HitCount);
                return ElsieResult.Text($"v{n}").WithETag($"\"v{n}\"");
            });

            Get("/cacheable-value", ctx =>
            {
                var n = Interlocked.Increment(ref HitCount);
                return ElsieResult.Text($"v{n}");
            });

            Get("/no-store-response", ctx =>
            {
                var n = Interlocked.Increment(ref HitCount);
                return ElsieResult.Text($"v{n}")
                    .WithHeader("Cache-Control", "no-store");
            });

            Get("/cookie-value", ctx =>
            {
                var n = Interlocked.Increment(ref HitCount);
                return ElsieResult.Text($"v{n}").WithCookie("session", "abc");
            });

            Get("/etag", ctx =>
            {
                var n = Interlocked.Increment(ref HitCount);
                return ElsieResult.Text($"v{n}").WithETag($"\"v{n}\"");
            });
        }
    }

    private static ElsieInMemoryHost CreateHost(Action<ElsieOutputCachingOptions>? options = null) =>
        ElsieInMemoryHost.Create(s =>
        {
            s.AddOutputCaching(options);
            s.AddElsieModule<CacheModule>();
        });

    /// <summary>A cached response is served without re-running the handler.</summary>
    [Fact]
    public async Task Second_request_served_from_cache()
    {
        CacheModule.HitCount = 0;
        await using var host = CreateHost();

        var first = await host.GetAsync("/cacheable-value");
        var firstCount = CacheModule.HitCount;
        var second = await host.GetAsync("/cacheable-value");

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Equal(first.ReadAsString(), second.ReadAsString());
        Assert.Equal(firstCount, CacheModule.HitCount); // handler did not run again
    }

    /// <summary>Accept-Encoding variants are cached as separate entries.</summary>
    [Fact]
    public async Task Accept_encoding_variants_cache_independently()
    {
        CacheModule.HitCount = 0;
        await using var host = CreateHost();

        var first = await host.SendAsync("GET", "/value", headers: new Dictionary<string, string>
        {
            ["Accept-Encoding"] = "gzip"
        });

        var second = await host.SendAsync("GET", "/value", headers: new Dictionary<string, string>
        {
            ["Accept-Encoding"] = "br"
        });
        var countAfterBoth = CacheModule.HitCount;

        // Different AE → different cache entries → handler ran for each.
        Assert.Equal(2, countAfterBoth);

        // Repeating an AE variant is served from cache (handler does not run again).
        var third = await host.SendAsync("GET", "/value", headers: new Dictionary<string, string>
        {
            ["Accept-Encoding"] = "gzip"
        });
        Assert.Equal(countAfterBoth, CacheModule.HitCount);
        Assert.Equal(first.ReadAsString(), third.ReadAsString());
    }

    /// <summary>A request with no-store is not served from cache.</summary>
    [Fact]
    public async Task No_store_request_skips_cache_and_runs_handler()
    {
        CacheModule.HitCount = 0;
        await using var host = CreateHost();

        var first = await host.GetAsync("/cacheable-value");
        var countAfterFirst = CacheModule.HitCount;

        var second = await host.SendAsync("GET", "/cacheable-value", headers: new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store"
        });
        Assert.True(CacheModule.HitCount > countAfterFirst, "no-store request must run the handler");
        Assert.Equal(first.StatusCode, second.StatusCode);
    }

    /// <summary>A response carrying Set-Cookie is per-user and must never be cached
    /// (a shared cache would replay one client's cookie to another).</summary>
    [Fact]
    public async Task Set_cookie_response_is_never_cached()
    {
        CacheModule.HitCount = 0;
        await using var host = CreateHost();

        var first = await host.GetAsync("/cookie-value");
        Assert.Equal(1, CacheModule.HitCount);
        Assert.Contains("session=abc", first.Headers.GetSingle("Set-Cookie"), StringComparison.Ordinal);

        var second = await host.GetAsync("/cookie-value");
        Assert.Equal(2, CacheModule.HitCount); // handler ran again — nothing served from cache
        Assert.Equal("v2", second.ReadAsString());
    }

    /// <summary>A response with no-store is not cached.</summary>
    [Fact]
    public async Task No_store_response_is_not_cached()
    {
        CacheModule.HitCount = 0;
        await using var host = CreateHost();

        await host.GetAsync("/no-store-response");
        var countAfterFirst = CacheModule.HitCount;

        await host.GetAsync("/no-store-response");
        Assert.True(CacheModule.HitCount > countAfterFirst, "no-store response must not be cached");
    }

    /// <summary>A conditional request against a cached ETag returns 304.</summary>
    [Fact]
    public async Task Conditional_request_against_cached_etag_returns_304()
    {
        CacheModule.HitCount = 0;
        await using var host = CreateHost();

        var first = await host.GetAsync("/etag");
        Assert.Equal(200, first.StatusCode);
        var etag = first.Headers["ETag"];
        Assert.NotNull(etag);

        var notModified = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = etag!
        });
        Assert.Equal(304, notModified.StatusCode);
        Assert.Equal(etag, notModified.Headers["ETag"]);
    }

    /// <summary>The LRU evicts the least-recently-used entry at the entry cap.</summary>
    [Fact]
    public async Task Lru_evicts_at_entry_cap()
    {
        CacheModule.HitCount = 0;
        await using var host = CreateHost(o => o.MaxEntries = 2);

        await host.GetAsync("/cacheable-value?a=1");
        await host.GetAsync("/cacheable-value?a=2");

        // A third distinct key evicts the least-recent (a=1).
        await host.GetAsync("/cacheable-value?a=3");
        var countAfterEvict = CacheModule.HitCount;

        // Re-requesting a=1 must run the handler again (it was evicted).
        await host.GetAsync("/cacheable-value?a=1");
        Assert.Equal(countAfterEvict + 1, CacheModule.HitCount);
    }
}
