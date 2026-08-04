using System.Text.Json;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class ResponseCachingTests
{
    private static readonly DateTimeOffset LastModified = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private const string LastModifiedHeader = "Tue, 02 Jan 2024 03:04:05 GMT";

    // Routes WITHOUT automatic middleware; handlers opt in explicitly via EvaluateConditional.
    private sealed class CacheModule : ElsieModule
    {
        public CacheModule()
        {
            Get("/explicit", ctx => ElsieResult.Text("hello")
                .WithETag("\"v1\"")
                .EvaluateConditional(ctx.Request));
            Get("/computed", () => ElsieResult.Text("hello world").WithComputedETag());
        }
    }

    // Routes WITH automatic conditional evaluation via module middleware.
    private sealed class AutoCacheModule : ElsieModule
    {
        public AutoCacheModule()
        {
            Use(ElsieCaching.ConditionalGet());
            Get("/etag", () => ElsieResult.Text("hello")
                .WithETag("\"v1\"")
                .WithCacheControl(c => c.Public().MaxAge(TimeSpan.FromSeconds(300))));
            Get("/weak", () => ElsieResult.Text("hello").WithETag("v1", weak: true));
            Get("/lastmod", () => ElsieResult.Text("hello")
                .WithETag("\"v1\"")
                .WithLastModified(LastModified));
            Get("/length", () => ElsieResult.Text("hello")
                .WithETag("\"v1\"")
                .WithHeader("Content-Length", "999"));
            Get("/plain", () => ElsieResult.Text("hello"));
            Get("/auto-computed", () => ElsieResult.Text("payload").WithComputedETag());
            Post("/create", () => ElsieResult.Status(201).WithETag("\"obj-1\""));
        }
    }

    // ---------------------------------------------------------------- Cache-Control

    [Fact]
    public void CacheControl_serializes_public_max_age()
    {
        var value = new ElsieCacheControl()
            .Public()
            .MaxAge(TimeSpan.FromMinutes(5))
            .ToString();
        Assert.Equal("public, max-age=300", value);
    }

    [Fact]
    public void CacheControl_serializes_directives_in_canonical_order()
    {
        Assert.Equal("no-store", new ElsieCacheControl().NoStore().ToString());
        Assert.Equal("no-cache", new ElsieCacheControl().NoCache().ToString());
        Assert.Equal("private, must-revalidate", new ElsieCacheControl().Private().MustRevalidate().ToString());
        Assert.Equal("immutable", new ElsieCacheControl().Immutable().ToString());
        Assert.Equal(
            "public, max-age=60, s-maxage=30",
            new ElsieCacheControl()
                .Public()
                .MaxAge(TimeSpan.FromSeconds(60))
                .SharedMaxAge(TimeSpan.FromSeconds(30))
                .ToString());
        Assert.Equal(
            "public, max-age=31536000, immutable",
            new ElsieCacheControl()
                .Public()
                .MaxAge(TimeSpan.FromDays(365))
                .Immutable()
                .ToString());
        Assert.Equal("public, max-age=0", new ElsieCacheControl().Public().MaxAge(TimeSpan.Zero).ToString());
    }

    [Fact]
    public void CacheControl_rejects_contradictions_and_empty_builders()
    {
        Assert.Throws<InvalidOperationException>(() => new ElsieCacheControl().Public().Private().ToString());
        Assert.Throws<InvalidOperationException>(() => new ElsieCacheControl().ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElsieCacheControl().MaxAge(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ElsieCacheControl().SharedMaxAge(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void WithCacheControl_sets_and_replaces_the_header()
    {
        var result = ElsieResult.Text("x")
            .WithHeader("Cache-Control", "no-store")
            .WithCacheControl(c => c.Public().MaxAge(TimeSpan.FromSeconds(60)));
        Assert.Equal("public, max-age=60", result.Headers["Cache-Control"]);
    }

    // ---------------------------------------------------------------- ETag helpers

    [Fact]
    public void WithETag_quotes_bare_values_and_honors_weak_flag()
    {
        Assert.Equal("\"v1\"", ElsieResult.Text("x").WithETag("v1").Headers["ETag"]);
        Assert.Equal("\"v1\"", ElsieResult.Text("x").WithETag("\"v1\"").Headers["ETag"]);
        Assert.Equal("W/\"v1\"", ElsieResult.Text("x").WithETag("v1", weak: true).Headers["ETag"]);
        Assert.Equal("W/\"v1\"", ElsieResult.Text("x").WithETag("W/\"v1\"").Headers["ETag"]);
        Assert.Equal("W/\"v1\"", ElsieResult.Text("x").WithETag("W/v1").Headers["ETag"]);
    }

    [Fact]
    public void WithETag_rejects_invalid_opaque_tags()
    {
        Assert.Throws<ArgumentException>(() => ElsieResult.Text("x").WithETag("has space"));
        Assert.Throws<ArgumentException>(() => ElsieResult.Text("x").WithETag("\"unbalanced"));
        Assert.Throws<ArgumentException>(() => ElsieResult.Text("x").WithETag("\"a\"b\""));
        Assert.Throws<ArgumentException>(() => ElsieResult.Text("x").WithETag("bad\u007f"));
    }

    [Fact]
    public void WithComputedETag_is_deterministic_strong_lowercase_hex()
    {
        var etag = ElsieResult.Text("hello world").WithComputedETag().Headers["ETag"];
        Assert.NotNull(etag);
        Assert.Equal(etag, ElsieResult.Text("hello world").WithComputedETag().Headers["ETag"]);

        // Known SHA-256 of "hello world".
        Assert.Equal("\"b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9\"", etag);
        Assert.StartsWith("\"", etag, StringComparison.Ordinal);
        Assert.EndsWith("\"", etag, StringComparison.Ordinal);
        Assert.DoesNotContain("W/", etag, StringComparison.Ordinal);
        var hex = etag[1..^1];
        Assert.Equal(64, hex.Length);
        Assert.All(hex, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.All(hex, c => Assert.False(char.IsUpper(c)));
    }

    [Fact]
    public void WithComputedETag_varies_with_body_and_requires_buffered_body()
    {
        var alpha = ElsieResult.Text("alpha").WithComputedETag().Headers["ETag"];
        var beta = ElsieResult.Text("beta").WithComputedETag().Headers["ETag"];
        Assert.NotEqual(alpha, beta);

        var streamed = ElsieResult.Stream((_, _) => Task.CompletedTask, "text/plain");
        var ex = Assert.Throws<InvalidOperationException>(() => streamed.WithComputedETag());
        Assert.Contains("buffered body", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithLastModified_formats_http_date()
    {
        var result = ElsieResult.Text("x").WithLastModified(LastModified);
        Assert.Equal(LastModifiedHeader, result.Headers["Last-Modified"]);
    }

    // ---------------------------------------------------------------- Explicit evaluation

    [Fact]
    public async Task EvaluateConditional_explicit_match_returns_304_empty_body_and_etag()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<CacheModule>());
        var response = await host.SendAsync("GET", "/explicit", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"v1\""
        });

        Assert.Equal(304, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("\"v1\"", response.Headers["ETag"]);
    }

    // ---------------------------------------------------------------- Automatic middleware

    [Fact]
    public async Task IfNoneMatch_match_returns_304_with_empty_body_and_etag()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"v1\""
        });

        Assert.Equal(304, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("\"v1\"", response.Headers["ETag"]);
        Assert.Equal("public, max-age=300", response.Headers["Cache-Control"]);
    }

    [Fact]
    public async Task IfNoneMatch_mismatch_returns_200_with_full_body()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"stale\""
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("hello", response.ReadAsString());
        Assert.Equal("\"v1\"", response.Headers["ETag"]);
    }

    [Fact]
    public async Task No_conditional_headers_leave_the_result_untouched()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.GetAsync("/etag");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("hello", response.ReadAsString());
        Assert.Equal("\"v1\"", response.Headers["ETag"]);
    }

    [Fact]
    public async Task IfNoneMatch_uses_weak_comparison()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        // Weak client tag vs strong server tag → match.
        var weakClient = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "W/\"v1\""
        });
        Assert.Equal(304, weakClient.StatusCode);

        // Strong client tag vs weak server tag → match (weak comparison is symmetric).
        var strongClient = await host.SendAsync("GET", "/weak", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"v1\""
        });
        Assert.Equal(304, strongClient.StatusCode);

        // Multi-entry list.
        var multi = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"x\", \"y\", \"v1\""
        });
        Assert.Equal(304, multi.StatusCode);
    }

    [Fact]
    public async Task IfMatch_uses_strong_comparison()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        var match = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-Match"] = "\"v1\""
        });
        Assert.Equal(200, match.StatusCode);

        // A weak tag never satisfies a strong precondition.
        var weak = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-Match"] = "W/\"v1\""
        });
        Assert.Equal(412, weak.StatusCode);

        var star = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-Match"] = "*"
        });
        Assert.Equal(200, star.StatusCode);
    }

    [Fact]
    public async Task IfMatch_mismatch_returns_412_problem_json()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-Match"] = "\"nope\""
        });

        Assert.Equal(412, response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", response.ContentType);
        using var doc = JsonDocument.Parse(response.ReadAsString());
        Assert.Equal(412, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Precondition Failed", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task IfModifiedSince_honored_for_get()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        var notModified = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-Modified-Since"] = LastModifiedHeader
        });
        Assert.Equal(304, notModified.StatusCode);
        Assert.Empty(notModified.Body);
        Assert.Equal(LastModifiedHeader, notModified.Headers["Last-Modified"]);

        var stale = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-Modified-Since"] = "Mon, 01 Jan 2024 00:00:00 GMT"
        });
        Assert.Equal(200, stale.StatusCode);
        Assert.Equal("hello", stale.ReadAsString());
    }

    [Fact]
    public async Task IfNoneMatch_takes_precedence_over_IfModifiedSince()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        // INM non-matching + IMS matching → 200: the IMS condition is ignored.
        var nonMatching = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"stale\"",
            ["If-Modified-Since"] = LastModifiedHeader
        });
        Assert.Equal(200, nonMatching.StatusCode);

        // INM matching + IMS stale → 304: the INM condition wins either way.
        var matching = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"v1\"",
            ["If-Modified-Since"] = "Mon, 01 Jan 2024 00:00:00 GMT"
        });
        Assert.Equal(304, matching.StatusCode);
    }

    [Fact]
    public async Task IfUnmodifiedSince_returns_412_when_modified()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        var ok = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-Unmodified-Since"] = LastModifiedHeader
        });
        Assert.Equal(200, ok.StatusCode);

        var modified = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-Unmodified-Since"] = "Mon, 01 Jan 2024 00:00:00 GMT"
        });
        Assert.Equal(412, modified.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_match_on_unsafe_method_returns_412()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        var match = await host.SendAsync("POST", "/create", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"obj-1\""
        });
        Assert.Equal(412, match.StatusCode);

        var mismatch = await host.SendAsync("POST", "/create", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"other\""
        });
        Assert.Equal(201, mismatch.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_star_matches_an_existing_representation_only()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        var represented = await host.SendAsync("GET", "/plain", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "*"
        });
        Assert.Equal(304, represented.StatusCode);
        Assert.Empty(represented.Body);
    }

    // ---------------------------------------------------------------- 304 wire shape

    [Fact]
    public async Task NotModified_drops_stale_Content_Length_and_body()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        var ok = await host.SendAsync("GET", "/length");
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal("999", ok.Headers["Content-Length"]);
        Assert.Equal("hello", ok.ReadAsString());

        var notModified = await host.SendAsync("GET", "/length", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"v1\""
        });
        Assert.Equal(304, notModified.StatusCode);
        Assert.Empty(notModified.Body);
        Assert.False(notModified.Headers.Contains("Content-Length"));
        Assert.Equal("\"v1\"", notModified.Headers["ETag"]);
    }

    [Fact]
    public async Task NotModified_materializes_without_body_via_dispatch_bake()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<AutoCacheModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var outcome = await dispatcher.DispatchAsync(new ElsieRequest(
            "GET",
            "/etag",
            headers: new Dictionary<string, string> { ["If-None-Match"] = "\"v1\"" },
            requestServices: sp));
        var baked = ElsieHttpResponse.FromDispatch(outcome)!;
        Assert.Equal(304, baked.StatusCode);
        Assert.Null(baked.Body);
        Assert.Null(baked.BodyWriter);
        Assert.Equal("\"v1\"", baked.Headers["ETag"]);
        Assert.Empty(await baked.BufferBodyAsync());
    }

    // ---------------------------------------------------------------- App-level registration

    [Fact]
    public async Task ConditionalGet_registers_through_AddElsieMiddleware()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            s.AddElsieMiddleware(p => p.Use(ElsieCaching.ConditionalGet()));
            s.AddElsieModule<CacheModule>();
        });

        var match = await host.SendAsync("GET", "/explicit", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = "\"v1\""
        });
        Assert.Equal(304, match.StatusCode);
        Assert.Empty(match.Body);
        Assert.Equal("\"v1\"", match.Headers["ETag"]);
    }

    [Fact]
    public async Task Computed_etag_end_to_end_is_stable_and_drives_304()
    {
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());

        var first = await host.GetAsync("/auto-computed");
        var second = await host.GetAsync("/auto-computed");
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(first.Headers["ETag"], second.Headers["ETag"]);

        var notModified = await host.SendAsync("GET", "/auto-computed", headers: new Dictionary<string, string>
        {
            ["If-None-Match"] = first.Headers["ETag"]!
        });
        Assert.Equal(304, notModified.StatusCode);
        Assert.Empty(notModified.Body);
    }

    // ------------------------------------------------- RFC 9110 §13.2.2 precedence combinations

    [Fact]
    public async Task IfMatch_failure_beats_IfNoneMatch_match_returns_412()
    {
        // Step 1 (If-Match false => 412) precedes step 3 (If-None-Match match => 304).
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.SendAsync("GET", "/etag", headers: new Dictionary<string, string>
        {
            ["If-Match"] = "\"v2\"",
            ["If-None-Match"] = "\"v1\""
        });

        Assert.Equal(412, response.StatusCode);
    }

    [Fact]
    public async Task IfUnmodifiedSince_failure_beats_IfNoneMatch_match_returns_412()
    {
        // Step 2 (If-Unmodified-Since false => 412) precedes step 3 (If-None-Match match => 304).
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-Unmodified-Since"] = "Mon, 01 Jan 2024 00:00:00 GMT",
            ["If-None-Match"] = "\"v1\""
        });

        Assert.Equal(412, response.StatusCode);
    }

    [Fact]
    public async Task IfUnmodifiedSince_failure_beats_IfNoneMatch_no_match_returns_412()
    {
        // Step 2 (If-Unmodified-Since false => 412) precedes step 3 passthrough (would be 200).
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-Unmodified-Since"] = "Mon, 01 Jan 2024 00:00:00 GMT",
            ["If-None-Match"] = "\"stale\""
        });

        Assert.Equal(412, response.StatusCode);
    }

    [Fact]
    public async Task IfNoneMatch_absent_IfModifiedSince_not_modified_returns_304()
    {
        // Step 4: only reachable when If-None-Match is absent.
        await using var host = ElsieInMemoryHost.Create(s => s.AddElsieModule<AutoCacheModule>());
        var response = await host.SendAsync("GET", "/lastmod", headers: new Dictionary<string, string>
        {
            ["If-Modified-Since"] = LastModifiedHeader
        });

        Assert.Equal(304, response.StatusCode);
    }
}
