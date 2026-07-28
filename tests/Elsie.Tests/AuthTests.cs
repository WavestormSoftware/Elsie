using Xunit;

namespace Elsie.Tests;

public class AuthTests
{
    [Fact]
    public void RequireApiKey_allows_get_by_default()
    {
        var hook = ElsieAuth.RequireApiKey("secret");
        var ctx = Context("GET", "/");
        Assert.Null(hook(ctx));
    }

    [Fact]
    public void RequireApiKey_blocks_post_without_key()
    {
        var hook = ElsieAuth.RequireApiKey("secret");
        var ctx = Context("POST", "/");
        var result = hook(ctx);
        Assert.NotNull(result);
        Assert.Equal(401, result!.StatusCode);
    }

    [Fact]
    public void RequireApiKey_allows_post_with_key()
    {
        var hook = ElsieAuth.RequireApiKey("secret");
        var ctx = Context(
            "POST",
            "/",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Api-Key"] = "secret"
            });
        Assert.Null(hook(ctx));
    }

    private static ElsieContext Context(
        string method,
        string path,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(
            new ElsieRequest(method, path, headers: headers),
            new ElsieResponse(),
            new Dictionary<string, string>());
}
