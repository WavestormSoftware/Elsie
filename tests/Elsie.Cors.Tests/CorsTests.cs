using System.Net;
using Elsie.Cors;
using Elsie.Middleware;
using Elsie.Testing;
using Elsie.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

    [Fact]
    public async Task Cors_origins_hot_reload_from_configuration()
    {
        var urls = $"http://127.0.0.1:{GetFreePort()}";
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Elsie:Urls"] = urls,
            ["Elsie:Cors:Policies:Default:Origins:0"] = "https://one.example",
            ["Elsie:Cors:Policies:Default:Methods:0"] = "GET"
        });
        builder.UseElsie(app =>
        {
            app.QuietConsole(false)
                .Configure(o => o.ScanEntryAssembly = false)
                .Services(s => s.AddElsieCors(builder.Configuration))
                .Module<ApiModule>();
        });

        using var host = builder.Build();
        await host.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(urls + "/") };

            using (var req = new HttpRequestMessage(HttpMethod.Get, "/hello"))
            {
                req.Headers.TryAddWithoutValidation("Origin", "https://one.example");
                using var res = await client.SendAsync(req);
                Assert.Equal("https://one.example", res.Headers.GetValues("Access-Control-Allow-Origin").First());
            }

            // Reload config to a different origin — the old origin is now denied.
            var root = (IConfigurationRoot)builder.Configuration;
            root.Providers.OfType<MemoryConfigurationProvider>()
                .First(p => p.TryGet("Elsie:Cors:Policies:Default:Origins:0", out _))
                .Set("Elsie:Cors:Policies:Default:Origins:0", "https://two.example");
            root.Reload();

            using (var req = new HttpRequestMessage(HttpMethod.Get, "/hello"))
            {
                req.Headers.TryAddWithoutValidation("Origin", "https://one.example");
                using var res = await client.SendAsync(req);
                Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
            }

            using (var req = new HttpRequestMessage(HttpMethod.Get, "/hello"))
            {
                req.Headers.TryAddWithoutValidation("Origin", "https://two.example");
                using var res = await client.SendAsync(req);
                var acao = res.Headers.GetValues("Access-Control-Allow-Origin").Single();
                Assert.Equal("https://two.example", acao);
            }
        }
        finally
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.StopAsync(cts.Token);
        }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Cors_middleware_handles_preflight_and_actuals()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieCors(o =>
        {
            o.AddDefaultPolicy(p => p
                .AllowOrigin("https://app.example")
                .AllowMethods("GET")
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10)));
        });
        services.AddElsieModule<ApiModule>();
        await using var sp = services.BuildServiceProvider();

        var pipeline = sp.GetRequiredService<ElsieMiddlewarePipeline>();
        pipeline.UseElsieCors();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        // Preflight: short-circuits before route matching.
        var preflight = await dispatcher.DispatchAsync(new ElsieRequest(
            "OPTIONS",
            "/hello",
            headers: new Dictionary<string, string>
            {
                ["Origin"] = "https://app.example",
                ["Access-Control-Request-Method"] = "GET"
            },
            requestServices: sp));
        Assert.Equal(204, preflight.Result!.StatusCode);
        Assert.Equal("https://app.example", preflight.Result.Headers["Access-Control-Allow-Origin"]);
        Assert.Equal("600", preflight.Result.Headers["Access-Control-Max-Age"]);

        // Actual request: ACAO applied on the way out.
        var actual = await dispatcher.DispatchAsync(new ElsieRequest(
            "GET",
            "/hello",
            headers: new Dictionary<string, string> { ["Origin"] = "https://app.example" },
            requestServices: sp));
        Assert.Equal(200, actual.Result!.StatusCode);
        Assert.Equal("https://app.example", actual.Result.Headers["Access-Control-Allow-Origin"]);

        // Disallowed origin → no ACAO header.
        var denied = await dispatcher.DispatchAsync(new ElsieRequest(
            "GET",
            "/hello",
            headers: new Dictionary<string, string> { ["Origin"] = "https://evil.example" },
            requestServices: sp));
        Assert.False(denied.Result!.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
