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
}
