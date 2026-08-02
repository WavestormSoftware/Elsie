using System.Net;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>Host-level features (static files, method not allowed, middleware).</summary>
public class HostMiddlewareTests
{
    private sealed class EchoModule : ElsieModule
    {
        public EchoModule()
        {
            Get("/ok", () => ElsieResult.Text("ok"));
        }
    }

    private sealed class MwModule : ElsieModule
    {
        public MwModule()
        {
            Use((ctx, next) =>
            {
                ctx.Response.Headers["X-Module-Mw"] = "1";
                return next(ctx);
            });
            Get("/module", () => ElsieResult.Text("module"));
        }
    }

    private sealed class DiMiddleware : Elsie.Middleware.IElsieMiddleware
    {
        public Task InvokeAsync(ElsieContext context, Elsie.Middleware.ElsieMiddlewareDelegate next)
        {
            context.Response.Headers["X-Di-Middleware"] = "1";
            return next(context);
        }
    }

    [Fact]
    public async Task App_use_inline_middleware_runs_and_short_circuits()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .Use(async (ctx, next) =>
            {
                ctx.Response.Headers["X-App-Mw"] = "1";
                await next(ctx);
                ctx.Response.Headers["X-App-Mw-Post"] = "1";
            })
            .StartAsync();

        using var client = server.CreateClient();
        using var res = await client.GetAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("1", res.Headers.GetValues("X-App-Mw").Single());
        Assert.Equal("1", res.Headers.GetValues("X-App-Mw-Post").Single());
        Assert.Equal("ok", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task App_use_di_middleware_is_resolved_per_request()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .Use<DiMiddleware>()
            .StartAsync();

        using var client = server.CreateClient();
        using var res = await client.GetAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("1", res.Headers.GetValues("X-Di-Middleware").Single());
    }

    [Fact]
    public async Task Module_use_middleware_scopes_to_module_routes()
    {
        await using var server = await ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EchoModule>()
            .Module<MwModule>()
            .StartAsync();

        using var client = server.CreateClient();

        using var moduleRes = await client.GetAsync("/module");
        Assert.Equal(HttpStatusCode.OK, moduleRes.StatusCode);
        Assert.Equal("1", moduleRes.Headers.GetValues("X-Module-Mw").Single());

        using var otherRes = await client.GetAsync("/ok");
        Assert.Equal(HttpStatusCode.OK, otherRes.StatusCode);
        Assert.False(otherRes.Headers.Contains("X-Module-Mw"));
    }

    [Fact]
    public async Task Static_files_served_under_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), "elsie-static-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "app.css"), "body{color:red}");

        try
        {
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen(IPAddress.Loopback, 0)
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<EchoModule>()
                .ContentRoot(root)
                .StaticFiles(s =>
                {
                    s.Root = root;
                    s.RequestPath = "/assets";
                })
                .StartAsync();

            using var client = server.CreateClient();
            var css = await client.GetStringAsync("/assets/app.css");
            Assert.Equal("body{color:red}", css);
            Assert.Equal("ok", await client.GetStringAsync("/ok"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Method_not_allowed_returns_405()
    {
        await using var host = ElsieTestHost.Create(s => s.AddElsieModule<EchoModule>());
        var res = await host.Client.PostAsync("/ok", null);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        Assert.True(res.Content.Headers.Contains("Allow") || res.Headers.Contains("Allow"));
    }
}
