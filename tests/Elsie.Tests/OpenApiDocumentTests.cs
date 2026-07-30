using System.Text.Json;
using Elsie.OpenApi;
using Elsie.Routing;
using Xunit;

namespace Elsie.Tests;

public class OpenApiDocumentTests
{
    private sealed record TodoDto(Guid Id, string Title, bool Done);
    private sealed record CreateTodo(string Title);
    private sealed record TodoQuery(string? Q, bool? Done);

    private sealed class SampleModule : ElsieModule
    {
        public SampleModule()
        {
            Get("/hello/{name}", () => ElsieResult.Text("ok"));
            Get("/items/{id:int}", () => ElsieResult.Text("ok"));
            Post("/items", () => ElsieResult.Status(201));
            Get("/files/{*path}", () => ElsieResult.Text("ok"));

            Get("/todos/{id:guid}", () => ElsieResult.Json(new TodoDto(Guid.Empty, "t", false)))
                .Named("getTodo")
                .WithSummary("Get todo")
                .WithDescription("Fetch one todo by id")
                .WithTags("todos")
                .Produces<TodoDto>()
                .Produces<ProblemDetailsDto>(404)
                .WithSecurity("ApiKey");

            Get("/todos", () => ElsieResult.Json(Array.Empty<TodoDto>()))
                .WithTags("todos")
                .AcceptsQuery<TodoQuery>()
                .Produces<TodoDto[]>();

            Post("/todos", () => ElsieResult.Status(201))
                .WithTags("todos")
                .Accepts<CreateTodo>()
                .Produces<TodoDto>(201)
                .WithSecurity("ApiKey");
        }
    }

    private sealed class CycleModule : ElsieModule
    {
        public CycleModule()
        {
            Get("/cycle", () => ElsieResult.Json(new Node())).Produces<Node>();
        }
    }

    private sealed class Node
    {
        public string Name { get; set; } = "";
        public Node? Child { get; set; }
    }

    private sealed record ProblemDetailsDto(int Status, string Title);

    [Fact]
    public void Builds_paths_and_parameters_from_routes()
    {
        var table = RouteTable.FromModules([new SampleModule()]);
        var doc = ElsieOpenApiDocument.Create(table, new ElsieOpenApiInfo { Title = "T", Version = "1" });

        Assert.Equal("3.0.3", doc["openapi"]);
        var paths = Assert.IsAssignableFrom<IDictionary<string, Dictionary<string, object>>>(doc["paths"]);
        Assert.True(paths.ContainsKey("/hello/{name}"));
        Assert.True(paths.ContainsKey("/items/{id}"));
        Assert.True(paths.ContainsKey("/files/{path}"));
        Assert.True(paths["/items"].ContainsKey("post"));

        var getItems = Assert.IsAssignableFrom<Dictionary<string, object>>(paths["/items/{id}"]["get"]);
        var parameters = Assert.IsAssignableFrom<List<Dictionary<string, object>>>(getItems["parameters"]);
        Assert.Equal("id", parameters[0]["name"]);
        var schema = Assert.IsAssignableFrom<Dictionary<string, object>>(parameters[0]["schema"]);
        Assert.Equal("integer", schema["type"]);
    }

    [Fact]
    public void Emits_metadata_schemas_security_and_query()
    {
        var info = new ElsieOpenApiInfo { Title = "T", Version = "1" };
        info.SecuritySchemes["ApiKey"] = ElsieOpenApiSecurityScheme.ApiKeyHeader();

        var table = RouteTable.FromModules([new SampleModule()]);
        var doc = ElsieOpenApiDocument.Create(table, info);
        var paths = Assert.IsAssignableFrom<IDictionary<string, Dictionary<string, object>>>(doc["paths"]);

        var getTodo = Assert.IsAssignableFrom<Dictionary<string, object>>(paths["/todos/{id}"]["get"]);
        Assert.Equal("getTodo", getTodo["operationId"]);
        Assert.Equal("Get todo", getTodo["summary"]);
        Assert.Equal("Fetch one todo by id", getTodo["description"]);
        Assert.Contains("todos", Assert.IsAssignableFrom<string[]>(getTodo["tags"]));
        Assert.True(getTodo.ContainsKey("security"));

        var responses = Assert.IsAssignableFrom<Dictionary<string, object>>(getTodo["responses"]);
        Assert.True(responses.ContainsKey("200"));
        Assert.True(responses.ContainsKey("404"));

        var list = Assert.IsAssignableFrom<Dictionary<string, object>>(paths["/todos"]["get"]);
        var qParams = Assert.IsAssignableFrom<List<Dictionary<string, object>>>(list["parameters"]);
        Assert.Contains(qParams, p => (string)p["name"] == "q" && (string)p["in"] == "query");

        var post = Assert.IsAssignableFrom<Dictionary<string, object>>(paths["/todos"]["post"]);
        var body = Assert.IsAssignableFrom<Dictionary<string, object>>(post["requestBody"]);
        Assert.True(body.ContainsKey("content"));

        var components = Assert.IsAssignableFrom<Dictionary<string, object?>>(doc["components"]);
        Assert.True(components.ContainsKey("schemas"));
        Assert.True(components.ContainsKey("securitySchemes"));
        var schemes = Assert.IsAssignableFrom<Dictionary<string, object>>(components["securitySchemes"]!);
        Assert.True(schemes.ContainsKey("ApiKey"));
    }

    [Fact]
    public void Cycle_throws_clear_error()
    {
        var table = RouteTable.FromModules([new CycleModule()]);
        var ex = Assert.Throws<InvalidOperationException>(() => ElsieOpenApiDocument.Create(table));
        Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(Node), ex.Message, StringComparison.Ordinal);
    }

    private sealed class ObjectProducesModule : ElsieModule
    {
        public ObjectProducesModule()
        {
            Get("/bag", () => ElsieResult.Json(new { a = 1 })).Produces<object>();
        }
    }

    [Fact]
    public void Produces_object_inlines_free_form_schema()
    {
        var table = RouteTable.FromModules([new ObjectProducesModule()]);
        var doc = ElsieOpenApiDocument.Create(table);
        var paths = Assert.IsAssignableFrom<IDictionary<string, Dictionary<string, object>>>(doc["paths"]);
        var get = Assert.IsAssignableFrom<Dictionary<string, object>>(paths["/bag"]["get"]);
        Assert.True(get.ContainsKey("responses"));
        var json = ElsieOpenApiDocument.ToJson(table);
        Assert.Contains("\"type\":\"object\"", json.Replace(" ", ""), StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_is_non_empty_and_roundtrips()
    {
        var table = RouteTable.FromModules([new SampleModule()]);
        var json = ElsieOpenApiDocument.ToJson(table);
        Assert.Contains("\"openapi\"", json, StringComparison.Ordinal);
        Assert.Contains("/hello/{name}", json, StringComparison.Ordinal);
        using var _ = JsonDocument.Parse(json);
    }
}
