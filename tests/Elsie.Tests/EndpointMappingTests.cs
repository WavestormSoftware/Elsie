using System.Text;
using System.Text.Json;
using Elsie.Endpoints;
using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class EndpointMappingTests
{
    private sealed record CreateUser(string Name, int Age);

    private sealed record UserView(int Id, string Name);

    private sealed class ServiceModule : ElsieModule
    {
    }

    private interface IGreeting
    {
        string Text { get; }
    }

    private sealed class Greeting : IGreeting
    {
        public string Text => "served";
    }

    private static ServiceProvider Build(Action<IElsieEndpointRouteBuilder> map, Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddElsieEndpoints(map, o => o.ScanEntryAssembly = false);
        services?.Invoke(collection);
        return collection.BuildServiceProvider();
    }

    private static async Task<(int Status, string Body, string? ContentType)> SendAsync(
        ServiceProvider provider,
        ElsieRequest request)
    {
        var dispatcher = provider.GetRequiredService<ElsieDispatcher>();
        var outcome = await dispatcher.DispatchAsync(request);
        var response = ElsieHttpResponse.FromDispatch(outcome);
        Assert.NotNull(response);
        var body = Encoding.UTF8.GetString(await response!.BufferBodyAsync());
        return (response.StatusCode, body, response.ContentType);
    }

    private static ElsieRequest Json(string method, string path, object value, IServiceProvider? requestServices = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        return new ElsieRequest(
            method,
            path,
            body: new MemoryStream(bytes),
            contentLength: bytes.Length,
            contentType: "application/json",
            requestServices: requestServices);
    }

    [Fact]
    public async Task MapGet_binds_route_value_and_returns_text()
    {
        await using var sp = Build(app => app.MapGet("/hello/{name}", (string name) => $"hi {name}"));

        var (status, body, contentType) = await SendAsync(sp, new ElsieRequest("GET", "/hello/ann"));

        Assert.Equal(200, status);
        Assert.Equal("hi ann", body);
        Assert.StartsWith("text/plain", contentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapGet_binds_query_values_and_defaults()
    {
        await using var sp = Build(app => app.MapGet(
            "/items",
            (int page, int size = 10) => ElsieResults.Ok(new { page, size })));

        var (status, body, contentType) = await SendAsync(
            sp,
            new ElsieRequest("GET", "/items", query: new Dictionary<string, string> { ["page"] = "2" }));

        Assert.Equal(200, status);
        Assert.Contains("\"page\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"size\":10", body, StringComparison.Ordinal);
        Assert.StartsWith("application/json", contentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapPost_binds_json_body_into_record()
    {
        await using var sp = Build(app => app.MapPost(
            "/users",
            (CreateUser body) => ElsieResults.Created(new UserView(1, body.Name), "/users/1")));

        var (status, body, _) = await SendAsync(sp, Json("POST", "/users", new { name = "ann", age = 30 }));

        Assert.Equal(201, status);
        Assert.Contains("\"name\":\"ann\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapPost_async_typed_result_is_awaited()
    {
        await using var sp = Build(app => app.MapPost(
            "/slow",
            async (CreateUser body) =>
            {
                await Task.Yield();
                return ElsieResults.Ok(new UserView(7, body.Name));
            }));

        var (status, body, _) = await SendAsync(sp, Json("POST", "/slow", new { name = "bo", age = 4 }));

        Assert.Equal(200, status);
        Assert.Contains("\"id\":7", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_required_route_value_returns_problem()
    {
        await using var sp = Build(app => app.MapGet("/strict", (int count) => ElsieResults.Ok(count)));

        var (status, body, contentType) = await SendAsync(sp, new ElsieRequest("GET", "/strict"));

        Assert.Equal(400, status);
        Assert.Contains("count", body, StringComparison.Ordinal);
        Assert.StartsWith("application/problem+json", contentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_prefix_filters_and_tags_apply_to_children()
    {
        await using var sp = Build(app =>
        {
            var api = app
                .MapGroup("/api")
                .WithTags("Api")
                .WithFilter((context, next) =>
                {
                    context.Context.Response.Headers["X-Group"] = "1";
                    return next(context);
                });

            api.MapGet("/ping", () => "pong");
        });

        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/api/ping"));
        var response = ElsieHttpResponse.FromDispatch(outcome)!;

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("pong", Encoding.UTF8.GetString(await response.BufferBodyAsync()));
        Assert.Equal("1", response.Headers["X-Group"]);

        var route = Assert.Single(sp.GetRequiredService<RouteTable>().Routes);
        Assert.Equal("/api/ping", route.Template);
        Assert.Contains("Api", route.Tags);
    }

    [Fact]
    public async Task Filter_can_short_circuit_the_handler()
    {
        var invoked = false;

        await using var sp = Build(app => app
            .MapGet("/guarded", () =>
            {
                invoked = true;
                return "handler";
            })
            .WithFilter((_, _) => new ValueTask<object?>(ElsieResults.Unauthorized())));

        var (status, _, _) = await SendAsync(sp, new ElsieRequest("GET", "/guarded"));

        Assert.Equal(401, status);
        Assert.False(invoked);
    }

    [Fact]
    public async Task Filter_can_rewrite_the_handler_result()
    {
        await using var sp = Build(app => app
            .MapGet("/tagged", () => "body")
            .WithFilter(async (context, next) =>
            {
                var value = await next(context);
                return value is ElsieResult result ? result.WithHeader("X-Filtered", "yes") : value;
            }));

        var dispatcher = sp.GetRequiredService<ElsieDispatcher>();
        var outcome = await dispatcher.DispatchAsync(new ElsieRequest("GET", "/tagged"));
        var response = ElsieHttpResponse.FromDispatch(outcome)!;

        Assert.Equal("yes", response.Headers["X-Filtered"]);
    }

    [Fact]
    public void RequireAuthorization_and_AllowAnonymous_attach_metadata()
    {
        using var sp = Build(app =>
        {
            app.MapGroup("/admin")
                .RequireAuthorization("admin")
                .MapGet("/stats", () => "ok");
            app.MapGet("/health", () => "ok").AllowAnonymous();
        });

        var routes = sp.GetRequiredService<RouteTable>().Routes;
        var stats = routes.Single(r => r.Template == "/admin/stats");
        var health = routes.Single(r => r.Template == "/health");

        var requirements = stats.GetMetadata<ElsieAuthorizationRequirements>();
        Assert.NotNull(requirements);
        Assert.True(requirements!.Required);
        Assert.Contains("admin", requirements.Policies);

        Assert.NotNull(health.GetMetadata<ElsieAllowAnonymousMetadata>());
        Assert.Null(health.GetMetadata<ElsieAuthorizationRequirements>());
    }

    [Fact]
    public async Task FromServices_resolves_from_request_scope()
    {
        await using var sp = Build(
            app => app.MapGet("/greet", ([FromServices] IGreeting greeting) => greeting.Text),
            services => services.AddScoped<IGreeting, Greeting>());

        using var scope = sp.CreateScope();
        var (status, body, _) = await SendAsync(
            sp,
            new ElsieRequest("GET", "/greet", requestServices: scope.ServiceProvider));

        Assert.Equal(200, status);
        Assert.Equal("served", body);
    }

    [Fact]
    public void OpenApi_infers_body_and_response_schemas()
    {
        using var sp = Build(app => app.MapPost("/users", (CreateUser body) => new UserView(1, body.Name)));

        var route = Assert.Single(sp.GetRequiredService<RouteTable>().Routes);

        Assert.Equal(typeof(CreateUser), route.AcceptsType);
        var produces = Assert.Single(route.Produces);
        Assert.Equal(typeof(UserView), produces.Type);
        Assert.Equal(200, produces.StatusCode);
    }

    [Fact]
    public async Task MapMethods_registers_each_verb()
    {
        await using var sp = Build(app => app.MapMethods(["GET", "DELETE"], "/thing", () => "ok"));

        var (getStatus, _, _) = await SendAsync(sp, new ElsieRequest("GET", "/thing"));
        var (deleteStatus, _, _) = await SendAsync(sp, new ElsieRequest("DELETE", "/thing"));

        Assert.Equal(200, getStatus);
        Assert.Equal(200, deleteStatus);
        Assert.Equal(2, sp.GetRequiredService<RouteTable>().Routes.Count);
    }

    [Fact]
    public async Task Invalid_json_body_returns_400()
    {
        await using var sp = Build(app => app.MapPost("/users", (CreateUser body) => body.Name));

        var bytes = Encoding.UTF8.GetBytes("{ not json");
        var request = new ElsieRequest(
            "POST",
            "/users",
            body: new MemoryStream(bytes),
            contentLength: bytes.Length,
            contentType: "application/json");

        var (status, body, _) = await SendAsync(sp, request);

        Assert.Equal(400, status);
        Assert.Contains("Invalid JSON", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_routes_and_mapped_endpoints_coexist()
    {
        var services = new ServiceCollection();
        services.AddElsieModule<ServiceModule>();
        services.AddElsieEndpoints(app => app.MapGet("/mapped", () => "mapped"), o => o.ScanEntryAssembly = false);
        await using var sp = services.BuildServiceProvider();

        var table = sp.GetRequiredService<RouteTable>();
        Assert.Contains(table.Routes, r => r.Template == "/mapped");
    }

    [Fact]
    public void Duplicate_endpoint_registration_fails_at_startup()
    {
        var services = new ServiceCollection();
        services.AddElsieEndpoints(
            app =>
            {
                app.MapGet("/dup", () => "one");
                app.MapGet("/dup", () => "two");
            },
            o => o.ScanEntryAssembly = false);

        using var sp = services.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<RouteTable>());
    }
}
