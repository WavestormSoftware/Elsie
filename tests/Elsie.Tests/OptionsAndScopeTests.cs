using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class OptionsAndScopeTests
{
    [Fact]
    public void AddElsie_repeat_calls_compose_options_on_same_instance()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsie(o => o.ImplicitHead = false);
        services.AddElsie(o => o.RouteConstraints["x"] = _ => true);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ElsieOptions>();
        Assert.False(options.ScanEntryAssembly);
        Assert.False(options.ImplicitHead);
        Assert.True(options.RouteConstraints.ContainsKey("x"));
    }

    [Fact]
    public void ElsieJson_DefaultOptions_is_stable_and_not_app_options()
    {
        var services = new ServiceCollection();
        services.AddElsie(o =>
        {
            o.ScanEntryAssembly = false;
            o.JsonSerializerOptions.WriteIndented = true;
        });
        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<ElsieOptions>();

        Assert.True(options.JsonSerializerOptions.WriteIndented);
        Assert.False(ElsieJson.DefaultOptions.WriteIndented);
        Assert.NotSame(options.JsonSerializerOptions, ElsieJson.DefaultOptions);
    }

    [Fact]
    public async Task Ctx_Json_uses_app_options_Result_Json_uses_framework_defaults()
    {
        var services = new ServiceCollection();
        services.AddElsie(o =>
        {
            o.ScanEntryAssembly = false;
            o.JsonSerializerOptions.WriteIndented = true;
        });
        services.AddElsieModule<JsonModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var appJson = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/app-json"));
        var appBody = System.Text.Encoding.UTF8.GetString(appJson.Result!.Body!.Value.Span);
        Assert.Contains('\n', appBody); // indented

        var staticJson = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/static-json"));
        var staticBody = System.Text.Encoding.UTF8.GetString(staticJson.Result!.Body!.Value.Span);
        Assert.DoesNotContain('\n', staticBody);
    }

    [Fact]
    public async Task Scoped_service_resolved_from_request_scope()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddScoped<ScopedStamp>();
        services.AddElsieModule<ScopedModule>();
        await using var sp = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        string a;
        string b;
        await using (var scope = sp.CreateAsyncScope())
        {
            var outcome = await dispatcher.DispatchAsync(new ElsieRequest(
                "GET",
                "/scoped",
                requestServices: scope.ServiceProvider));
            a = System.Text.Encoding.UTF8.GetString(outcome.Result!.Body!.Value.Span);
        }

        await using (var scope = sp.CreateAsyncScope())
        {
            var outcome = await dispatcher.DispatchAsync(new ElsieRequest(
                "GET",
                "/scoped",
                requestServices: scope.ServiceProvider));
            b = System.Text.Encoding.UTF8.GetString(outcome.Result!.Body!.Value.Span);
        }

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ValidateScopes_rejects_scoped_from_singleton_ctor()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddScoped<ScopedStamp>();
        services.AddElsieModule<BadSingletonModule>();

        var ex = Assert.ThrowsAny<Exception>(() =>
            services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            }));
        Assert.Contains("scoped", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class JsonModule : ElsieModule
    {
        public JsonModule()
        {
            Get("/app-json", ctx => ctx.Json(new { a = 1 }));
            Get("/static-json", _ => ElsieResult.Json(new { a = 1 }));
        }
    }

    private sealed class ScopedStamp
    {
        public string Value { get; } = Guid.NewGuid().ToString("n");
    }

    private sealed class ScopedModule : ElsieModule
    {
        public ScopedModule()
        {
            Get("/scoped", ctx => ElsieResult.Text(ctx.Services.GetRequiredService<ScopedStamp>().Value));
        }
    }

    private sealed class BadSingletonModule : ElsieModule
    {
        public BadSingletonModule(ScopedStamp stamp)
        {
            Get("/bad", () => ElsieResult.Text(stamp.Value));
        }
    }
}
