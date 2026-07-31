using System.Net;
using Elsie.Cors;
using Elsie.Testing;
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
        ElsieTestHost.Create(services =>
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
        Assert.Equal("https://app.example", res.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }
}
