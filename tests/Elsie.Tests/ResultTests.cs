using System.Text.Json;
using Xunit;

namespace Elsie.Tests;

public class ResultTests
{
    [Fact]
    public void BadRequest_is_problem_json()
    {
        var result = ElsieResult.BadRequest("nope");
        Assert.Equal(400, result.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", result.ContentType);
        Assert.True(result.Body.HasValue);
        using var doc = JsonDocument.Parse(result.Body!.Value);
        Assert.Equal(400, doc.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Bad Request", doc.RootElement.GetProperty("title").GetString());
        Assert.Equal("nope", doc.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public void NotFound_without_detail_omits_detail_property()
    {
        var result = ElsieResult.NotFound();
        using var doc = JsonDocument.Parse(result.Body!.Value);
        Assert.Equal(404, doc.RootElement.GetProperty("status").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("detail", out _));
    }
}
