using System.Net;
using System.Text.Json;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Web.Tests;

public class HostingTests
{
    private interface IClock
    {
        string Stamp { get; }
    }

    private sealed class FixedClock : IClock
    {
        public string Stamp => "t0";
    }

    private sealed class HelloModule : ElsieModule
    {
        public HelloModule()
        {
            Get("/hello/{name}", ctx => ElsieResult.Text($"Hello {ctx.RouteValues["name"]}"));
            Get("/health", ctx => ctx.Json(new HealthDto("ok")));
            Get("/items/{id:int}", ctx => ElsieResult.Text(ctx.RouteValues["id"]));
            Post("/items", () => ElsieResult.Status(201));
            Get("/files/{*path}", ctx => ElsieResult.Text(ctx.RouteValues["path"]));
            Get("/go", () => ElsieResult.Redirect("/hello/redirected"));
            Post("/echo", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<EchoDto>(ct);
                if (!bind.IsSuccess) return bind.Error!;
                return ctx.Json(bind.Value);
            });
            Get("/di", ctx =>
            {
                var clock = ctx.GetRequiredService<IClock>();
                return ElsieResult.Text(clock.Stamp);
            });
        }
    }

    private sealed class CtorDiModule : ElsieModule
    {
        public CtorDiModule(IClock clock)
        {
            Get("/ctor-di", () => ElsieResult.Text(clock.Stamp));
        }
    }

    private sealed class ApiModule : ElsieModule
    {
        public ApiModule()
        {
            Path("/api");
            Group("/things", () =>
            {
                Get("/", () => ElsieResult.Text("list"));
                Post("/", async (ctx, ct) =>
                {
                    var bind = await ctx.BindJsonAsync<EchoDto>(ct);
                    if (!bind.IsSuccess)
                    {
                        return bind.Error!;
                    }

                    if (string.IsNullOrWhiteSpace(bind.Value!.Message))
                    {
                        return ElsieResult.BadRequest("Message is required.");
                    }

                    return ctx.Json(bind.Value, statusCode: 201);
                });
                Get("/boom", _ => throw new InvalidOperationException("kaboom"));
                Get("/missing", _ => throw new KeyNotFoundException("nope"));
            });
        }
    }

    private sealed class GuardedModule : ElsieModule
    {
        public GuardedModule()
        {
            Before(ElsieAuth.RequireHeader("X-Api-Key", "secret"));
            Get("/guarded", () => ElsieResult.Text("ok"));
        }
    }

    private sealed record HealthDto(string Status);
    private sealed record EchoDto(string Message);

    [Fact]
    public async Task Get_route_param()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddSingleton<IClock, FixedClock>();
            s.AddElsieModule<HelloModule>();
        });
        Assert.Equal("Hello Ada", await host.Client.GetStringAsync("/hello/Ada"));
    }

    [Fact]
    public async Task Get_json()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var json = await host.Client.GetStringAsync("/health");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Constraint_and_post_status()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        Assert.Equal("42", await host.Client.GetStringAsync("/items/42"));
        var post = await host.Client.PostAsync("/items", null);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
    }

    [Fact]
    public async Task Catch_all_and_redirect()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        Assert.Equal("a/b", await host.Client.GetStringAsync("/files/a/b"));

        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
        using var client = new HttpClient(handler) { BaseAddress = host.Client.BaseAddress };
        var res = await client.GetAsync("/go");
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/hello/redirected", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Bind_json_and_di()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddSingleton<IClock, FixedClock>();
            s.AddElsieModule<HelloModule>();
            s.AddElsieModule<CtorDiModule>();
        });

        var echo = await host.PostJsonAsync("/echo", new EchoDto("hi"));
        echo.EnsureSuccessStatusCode();
        Assert.Equal("t0", await host.Client.GetStringAsync("/di"));
        Assert.Equal("t0", await host.Client.GetStringAsync("/ctor-di"));
    }

    [Fact]
    public async Task Path_group_and_exception_map()
    {
        await using var host = ElsieTestHost.Create(s =>
        {
            s.AddElsie(o =>
            {
                o.ScanEntryAssembly = false;
                o.MapException<KeyNotFoundException>((_, ex) => ElsieResult.NotFound(ex.Message));
            });
            s.AddElsieModule<ApiModule>();
        });

        Assert.Equal("list", await host.Client.GetStringAsync("/api/things/"));
        var missing = await host.Client.GetAsync("/api/things/missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Auth_header_gate()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<GuardedModule>());
        var denied = await host.Client.GetAsync("/guarded");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        using var okReq = new HttpRequestMessage(HttpMethod.Get, "/guarded");
        okReq.Headers.TryAddWithoutValidation("X-Api-Key", "secret");
        var ok = await host.SendAsync(okReq);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_served()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<HelloModule>()
            .OpenApi(o =>
            {
                o.Info.Title = "Test";
                o.UiPath = "/scalar";
            })
            .StartAsync();

        using var client = server.CreateClient();
        var openapi = await client.GetStringAsync("/openapi.json");
        Assert.Contains("openapi", openapi, StringComparison.OrdinalIgnoreCase);
        var ui = await client.GetAsync("/scalar");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }

    [Fact]
    public async Task OpenApi_offline_scalar_served_from_embedded_resources()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<HelloModule>()
            .OpenApi(o =>
            {
                o.Info.Title = "Test";
                o.UiPath = "/scalar";
                o.UseScalarCdn = false;
            })
            .StartAsync();

        using var client = server.CreateClient();
        var ui = await client.GetStringAsync("/scalar");
        // Offline page references the local bundle, never the CDN.
        Assert.DoesNotContain("jsdelivr", ui, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("./standalone.js", ui, StringComparison.Ordinal);

        var js = await client.GetAsync("/scalar/standalone.js");
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Equal("application/javascript", js.Content.Headers.ContentType?.MediaType);
        var bundle = await js.Content.ReadAsStringAsync();
        Assert.Contains("api-reference", bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotFound_returns_problem()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<HelloModule>());
        var res = await host.Client.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
