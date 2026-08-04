using Elsie.Routing;
using Xunit;

namespace Elsie.Tests;

public class SecurityHygieneTests
{
    [Fact]
    public void Headers_reject_crlf_in_name_and_value()
    {
        var h = new ElsieHeaders();
        // ElsieHeaderValidationException is an ArgumentException subtype (still an argument
        // error for callers), so ThrowsAny asserts the contract without demanding the exact type.
        Assert.ThrowsAny<ArgumentException>(() => h.Set("X-A\rB", "1"));
        Assert.ThrowsAny<ArgumentException>(() => h.Set("X-A", "1\n2"));
        Assert.ThrowsAny<ArgumentException>(() => h.Add("X-A", "1\0z"));
        h.Set("X-Ok", "fine");
        Assert.Equal("fine", h["X-Ok"]);
    }

    [Fact]
    public void Cookie_path_and_domain_reject_injection()
    {
        Assert.Throws<ArgumentException>(() =>
            ElsieResult.Text("x").WithCookie("a", "b", new ElsieCookieOptions { Path = "/\r\nX:1" }));
        Assert.Throws<ArgumentException>(() =>
            ElsieResult.Text("x").WithCookie("a", "b", new ElsieCookieOptions { Domain = "evil.com; HttpOnly" }));
    }

    [Fact]
    public void File_download_name_rejects_crlf()
    {
        Assert.Throws<ArgumentException>(() =>
            ElsieResult.File("x"u8.ToArray(), "text/plain", downloadName: "a\r\n.txt"));
    }

    [Fact]
    public void Route_table_tolerates_invalid_percent_encoding()
    {
        var table = RouteTable.FromModules([new M()]);
        // Must not throw
        var lookup = table.Lookup("GET", "/%zz");
        Assert.Equal(RouteLookupStatus.NotFound, lookup.Status);
    }

    private sealed class M : ElsieModule
    {
        public M() => Get("/ok", () => ElsieResult.Text("ok"));
    }
}
