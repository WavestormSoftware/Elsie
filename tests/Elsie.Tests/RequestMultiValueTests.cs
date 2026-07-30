using Xunit;

namespace Elsie.Tests;

public class RequestMultiValueTests
{
    [Fact]
    public void GetQueryValues_returns_all_values()
    {
        var request = new ElsieRequest(
            "GET",
            "/",
            queryValues: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["tag"] = new[] { "a", "b" }
            });

        Assert.Equal("a", request.GetQuery("tag"));
        Assert.Equal(new[] { "a", "b" }, request.GetQueryValues("tag"));
        Assert.Equal("a", request.Query["tag"]);
    }

    [Fact]
    public void GetQueryValues_falls_back_to_single_query()
    {
        var request = new ElsieRequest(
            "GET",
            "/",
            query: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["q"] = "one" });

        Assert.Equal(new[] { "one" }, request.GetQueryValues("q"));
        Assert.Empty(request.GetQueryValues("missing"));
    }

    [Fact]
    public void GetHeaderValues_and_cookie()
    {
        var request = new ElsieRequest(
            "GET",
            "/",
            headerValues: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Forwarded-For"] = new[] { "1.1.1.1", "2.2.2.2" },
                ["Cookie"] = new[] { "a=1; session=abc; b=2" }
            });

        Assert.Equal(new[] { "1.1.1.1", "2.2.2.2" }, request.GetHeaderValues("X-Forwarded-For"));
        Assert.Equal("1.1.1.1", request.GetHeader("X-Forwarded-For"));
        Assert.Equal("abc", request.GetCookie("session"));
        Assert.Null(request.GetCookie("missing"));
    }

    [Fact]
    public void First_wins_derived_from_values_only()
    {
        var request = new ElsieRequest(
            "GET",
            "/",
            queryValues: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = new[] { "1", "2" }
            });

        Assert.Equal("1", request.GetQuery("id"));
        Assert.Equal(2, request.GetQueryValues("id").Count);
    }

    [Fact]
    public void Host_fields_and_query_string()
    {
        var request = new ElsieRequest(
            "GET",
            "/x",
            scheme: "https",
            host: "example.com",
            pathBase: "/app",
            protocol: "HTTP/2",
            remoteIp: "1.2.3.4",
            queryString: "?q=1",
            queryValues: new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["q"] = new[] { "1" }
            });

        Assert.Equal("https", request.Scheme);
        Assert.Equal("example.com", request.Host);
        Assert.Equal("/app", request.PathBase);
        Assert.Equal("HTTP/2", request.Protocol);
        Assert.Equal("1.2.3.4", request.RemoteIp);
        Assert.Equal("?q=1", request.QueryString);
    }

    [Fact]
    public async Task ReadTextAsync_and_BufferBodyAsync()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("hello");
        await using var stream = new MemoryStream(bytes);
        var request = new ElsieRequest("POST", "/", body: stream, contentLength: bytes.Length);
        Assert.Equal("hello", await request.ReadTextAsync());
        Assert.Equal(bytes, await request.BufferBodyAsync());
    }
}
