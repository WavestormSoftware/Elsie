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

    [Fact]
    public void RequireBearer_blocks_without_token()
    {
        var hook = ElsieAuth.RequireBearer();
        var result = hook(Context("GET", "/"));
        Assert.NotNull(result);
        Assert.Equal(401, result!.StatusCode);
    }

    [Fact]
    public void RequireBearer_validates_token()
    {
        var hook = ElsieAuth.RequireBearer(t => t == "good");
        var bad = Context(
            "GET",
            "/",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer bad"
            });
        Assert.Equal(401, hook(bad)!.StatusCode);

        var good = Context(
            "GET",
            "/",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Authorization"] = "Bearer good"
            });
        Assert.Null(hook(good));
    }

    [Fact]
    public void RequireCookie_checks_name_and_value()
    {
        var hook = ElsieAuth.RequireCookie("session", v => v == "ok");
        var missing = Context("GET", "/");
        Assert.Equal(401, hook(missing)!.StatusCode);

        var ok = Context(
            "GET",
            "/",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cookie"] = "session=ok; other=1"
            });
        Assert.Null(hook(ok));
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
