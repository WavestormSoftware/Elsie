using System.Net;
using Elsie.Testing;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Host-level tests for <see cref="ElsieOutputCachingAppExtensions.UseOutputCaching"/>: a cached
/// response is served without re-running the handler, and a conditional request against a cached
/// ETag returns 304 over real HTTP.
/// </summary>
public class OutputCachingHostTests
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
        }
    }

    private static async Task<ElsieTestServer> StartServerAsync()
    {
        return await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .UseOutputCaching()
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<CacheModule>()
            .StartAsync();
    }

    [Fact]
    public async Task Cached_response_served_without_rerunning_handler()
    {
        CacheModule.HitCount = 0;
        await using var server = await StartServerAsync();
        using var client = server.CreateClient();

        var first = await client.GetAsync("/value");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var countAfterFirst = CacheModule.HitCount;

        var second = await client.GetAsync("/value");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(countAfterFirst, CacheModule.HitCount); // handler did not run again
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Conditional_request_against_cached_etag_returns_304()
    {
        CacheModule.HitCount = 0;
        await using var server = await StartServerAsync();
        using var client = server.CreateClient();

        var first = await client.GetAsync("/value");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(client.BaseAddress!, "/value"));
        req.Headers.TryAddWithoutValidation("If-None-Match", etag.ToString());
        using var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotModified, res.StatusCode);
        Assert.Equal(etag.ToString(), res.Headers.ETag?.ToString());
    }
}
