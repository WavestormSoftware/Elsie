using Elsie.OpenApi;
using Elsie.Routing;
using Xunit;

namespace Elsie.Tests;

public class OpenApiDocumentTests
{
    private sealed class SampleModule : ElsieModule
    {
        public SampleModule()
        {
            Get("/hello/{name}", () => ElsieResult.Text("ok"));
            Get("/items/{id:int}", () => ElsieResult.Text("ok"));
            Post("/items", () => ElsieResult.Status(201));
            Get("/files/{*path}", () => ElsieResult.Text("ok"));
        }
    }

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
    public void ToJson_is_non_empty()
    {
        var table = RouteTable.FromModules([new SampleModule()]);
        var json = ElsieOpenApiDocument.ToJson(table);
        Assert.Contains("\"openapi\"", json, StringComparison.Ordinal);
        Assert.Contains("/hello/{name}", json, StringComparison.Ordinal);
    }
}
