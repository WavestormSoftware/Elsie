using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class BindingTests
{
    private sealed class BindModule : ElsieModule
    {
        public BindModule()
        {
            Get("/r/{id:int}/{flag:bool}", ctx =>
            {
                if (!ctx.RequireRoute<int>("id", out var id, out var err)) return err!;
                if (!ctx.TryRoute<bool>("flag", out var flag)) return ElsieResult.BadRequest();
                return ElsieResult.Text($"{id}:{flag}");
            });

            Get("/q", ctx =>
            {
                if (!ctx.RequireQuery<int>("n", out var n, out var err)) return err!;
                return ElsieResult.Text(n.ToString());
            });

            Get("/bind-route/{Name}/{Age:int}", ctx =>
            {
                var bind = ctx.BindRoute<Person>();
                return bind.IsSuccess ? ElsieResult.Text($"{bind.Value!.Name}:{bind.Value.Age}") : bind.Error!;
            });

            Get("/bind-query", ctx =>
            {
                var bind = ctx.BindQuery<Person>();
                return bind.IsSuccess ? ElsieResult.Text($"{bind.Value!.Name}:{bind.Value.Age}") : bind.Error!;
            });

            Post("/form", async (ctx, ct) =>
            {
                var bind = await ctx.BindFormAsync<Person>(ct);
                return bind.IsSuccess ? ElsieResult.Text($"{bind.Value!.Name}:{bind.Value.Age}") : bind.Error!;
            });

            Post("/json", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<Person>(ct);
                return bind.IsSuccess ? ctx.Json(bind.Value) : bind.Error!;
            });

            Get("/bind-tags", ctx =>
            {
                var bind = ctx.BindQuery<Tagged>();
                return bind.IsSuccess
                    ? ElsieResult.Text(string.Join(',', bind.Value!.Tags ?? Array.Empty<string>()))
                    : bind.Error!;
            });

            Get("/problem", ctx => ctx.Problem(400, "Bad Request", "nope"));
        }
    }

    private sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    private sealed class Tagged
    {
        public string[]? Tags { get; set; }
    }

    private static async Task<(ElsieDispatcher Dispatcher, ServiceProvider Sp)> CreateAsync()
    {
        var services = new ServiceCollection();
        services.AddElsie(o => o.ScanEntryAssembly = false);
        services.AddElsieModule<BindModule>();
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ElsieDispatcher>(), sp);
    }

    [Fact]
    public async Task Route_and_query_typed_accessors()
    {
        var (dispatcher, sp) = await CreateAsync();
        await using (sp)
        {
            var r = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/r/7/true"));
            Assert.Equal("7:True", Encoding.UTF8.GetString(r.Result!.Body!.Value.Span));

            var q = await dispatcher.DispatchAsync(new ElsieRequest(
                "GET",
                "/q",
                query: new Dictionary<string, string> { ["n"] = "3" }));
            Assert.Equal("3", Encoding.UTF8.GetString(q.Result!.Body!.Value.Span));

            var bad = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/q"));
            Assert.Equal(400, bad.Result!.StatusCode);
        }
    }

    [Fact]
    public async Task BindRoute_and_BindQuery()
    {
        var (dispatcher, sp) = await CreateAsync();
        await using (sp)
        {
            var route = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/bind-route/Ada/36"));
            Assert.Equal("Ada:36", Encoding.UTF8.GetString(route.Result!.Body!.Value.Span));

            var query = await dispatcher.DispatchAsync(new ElsieRequest(
                "GET",
                "/bind-query",
                query: new Dictionary<string, string> { ["Name"] = "Bob", ["Age"] = "20" }));
            Assert.Equal("Bob:20", Encoding.UTF8.GetString(query.Result!.Body!.Value.Span));

            var bad = await dispatcher.DispatchAsync(new ElsieRequest(
                "GET",
                "/bind-query",
                query: new Dictionary<string, string> { ["Name"] = "Bob", ["Age"] = "nope" }));
            Assert.Equal(400, bad.Result!.StatusCode);
            Assert.Contains("errors", Encoding.UTF8.GetString(bad.Result.Body!.Value.Span), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task BindFormAsync_url_encoded()
    {
        var (dispatcher, sp) = await CreateAsync();
        await using (sp)
        {
            var bytes = Encoding.UTF8.GetBytes("Name=Eve&Age=22");
            await using var body = new MemoryStream(bytes);
            var outcome = await dispatcher.DispatchAsync(new ElsieRequest(
                "POST",
                "/form",
                body: body,
                contentLength: bytes.Length,
                contentType: "application/x-www-form-urlencoded"));
            Assert.Equal(200, outcome.Result!.StatusCode);
            Assert.Equal("Eve:22", Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span));
        }
    }

    [Fact]
    public async Task BindJson_max_size_and_path_error()
    {
        var services = new ServiceCollection();
        services.AddElsie(o =>
        {
            o.ScanEntryAssembly = false;
            o.MaxBindBodySize = 16;
        });
        services.AddElsieModule<BindModule>();
        await using var sp = services.BuildServiceProvider();
        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

        var big = Encoding.UTF8.GetBytes("{\"Name\":\"abcdefghijklmnop\",\"Age\":1}");
        await using var body = new MemoryStream(big);
        var outcome = await dispatcher.DispatchAsync(new ElsieRequest(
            "POST",
            "/json",
            body: body,
            contentLength: big.Length,
            contentType: "application/json"));
        Assert.Equal(400, outcome.Result!.StatusCode);
        Assert.Contains("max size", Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BindQuery_multi_value_array()
    {
        var (dispatcher, sp) = await CreateAsync();
        await using (sp)
        {
            var req = new ElsieRequest(
                "GET",
                "/bind-tags",
                queryValues: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Tags"] = new[] { "a", "b" }
                });
            var outcome = await dispatcher.DispatchAsync(req);
            Assert.Equal(200, outcome.Result!.StatusCode);
            Assert.Equal("a,b", Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span));
        }
    }

    [Fact]
    public async Task BindJson_rejects_non_json_content_type()
    {
        var (dispatcher, sp) = await CreateAsync();
        await using (sp)
        {
            var bytes = Encoding.UTF8.GetBytes("{\"Name\":\"x\",\"Age\":1}");
            await using var body = new MemoryStream(bytes);
            var outcome = await dispatcher.DispatchAsync(new ElsieRequest(
                "POST",
                "/json",
                body: body,
                contentLength: bytes.Length,
                contentType: "text/plain"));
            Assert.Equal(415, outcome.Result!.StatusCode);
        }
    }

    [Fact]
    public async Task Context_Problem_includes_instance()
    {
        var (dispatcher, sp) = await CreateAsync();
        await using (sp)
        {
            var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/problem"));
            Assert.Equal(400, outcome.Result!.StatusCode);
            var json = Encoding.UTF8.GetString(outcome.Result.Body!.Value.Span);
            Assert.Contains("\"instance\":\"/problem\"", json, StringComparison.Ordinal);
        }
    }
}
