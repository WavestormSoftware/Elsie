using System.Net;
using Elsie.Web;
using Elsie.Cors;
using Elsie.Testing;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Elsie.Cors.Tests;

public class CorsTests
{
    private sealed class ApiModule : ElsieModule
    {
        public ApiModule()
        {
            Get("/hello", () => ElsieResult.Text("hi"));
            Get("/private", () => ElsieResult.Text("secret"))
                .WithCors("tight");
        }
    }

    private static ElsieTestHost CreateHost() =>
        ElsieTestHost.Create(
            services =>
            {
                services.AddElsieCors(o =>
                {
                    o.AddDefaultPolicy(p => p
                        .AllowOrigin("https://app.example")
                        .AllowMethods("GET", "POST")
                        .AllowHeaders("Content-Type", "X-Test")
                        .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
                    o.AddPolicy("tight", p => p
                        .AllowOrigin("https://admin.example")
                        .AllowMethod("GET"));
                });
                services.AddElsieModule<ApiModule>();
            },
            app =>
            {
                app.UseElsieCors();
                app.MapElsie();
            });

    [Fact]
    public async Task Preflight_returns_cors_headers()
    {
        await using var host = CreateHost();
        using var req = new HttpRequestMessage(HttpMethod.Options, "/hello");
        req.Headers.TryAddWithoutValidation("Origin", "https://app.example");
        req.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        req.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "Content-Type");

        var res = await host.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("https://app.example", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("GET", res.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("600", res.Headers.GetValues("Access-Control-Max-Age").Single());
    }

    [Fact]
    public async Task Preflight_rejects_unknown_origin_without_acao()
    {
        await using var host = CreateHost();
        using var req = new HttpRequestMessage(HttpMethod.Options, "/hello");
        req.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        req.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        var res = await host.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Actual_get_includes_acao()
    {
        await using var host = CreateHost();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/hello");
        req.Headers.TryAddWithoutValidation("Origin", "https://app.example");

        var res = await host.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("hi", await res.Content.ReadAsStringAsync());
        Assert.Equal("https://app.example", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task WithCors_selects_named_policy_on_preflight()
    {
        await using var host = CreateHost();
        using var req = new HttpRequestMessage(HttpMethod.Options, "/private");
        req.Headers.TryAddWithoutValidation("Origin", "https://admin.example");
        req.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");

        var res = await host.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal("https://admin.example", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task WithCors_named_policy_on_actual()
    {
        await using var host = CreateHost();
        using var ok = new HttpRequestMessage(HttpMethod.Get, "/private");
        ok.Headers.TryAddWithoutValidation("Origin", "https://admin.example");
        var resOk = await host.SendAsync(ok);
        Assert.Equal(HttpStatusCode.OK, resOk.StatusCode);
        Assert.Equal("https://admin.example", resOk.Headers.GetValues("Access-Control-Allow-Origin").Single());

        using var bad = new HttpRequestMessage(HttpMethod.Get, "/private");
        bad.Headers.TryAddWithoutValidation("Origin", "https://app.example");
        var resBad = await host.SendAsync(bad);
        Assert.Equal(HttpStatusCode.OK, resBad.StatusCode);
        Assert.False(resBad.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void Credentials_with_any_origin_throws()
    {
        var o = new ElsieCorsOptions();
        Assert.Throws<InvalidOperationException>(() =>
            o.AddDefaultPolicy(p => p.AllowOrigin("*").AllowCredentials()));
    }
}
