using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

public class StaticAssetTests
{
    private sealed class EmptyModule : ElsieModule
    {
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "elsie-assets-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static Task<ElsieTestServer> StartAsync(string root, Action<ElsieStaticFileOptions> configure) =>
        ElsieApp.Create()
            .QuietConsole(false)
            .Listen(IPAddress.Loopback, 0)
            .Configure(o => o.ScanEntryAssembly = false)
            .Module<EmptyModule>()
            .ContentRoot(root)
            .StaticFiles(configure)
            .StartAsync();

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> SendAsync(
        HttpClient client,
        string path,
        params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body, response);
    }

    [Fact]
    public async Task Brotli_variant_is_served_when_accepted()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "original");
            await Task.Delay(10);
            await File.WriteAllTextAsync(Path.Combine(root, "app.js.br"), "brotli-bytes");
            await File.WriteAllTextAsync(Path.Combine(root, "app.js.gz"), "gzip-bytes");

            await using var server = await StartAsync(root, s => s.Root = root);
            using var client = server.CreateClient();

            var result = await SendAsync(client, "/app.js", ("Accept-Encoding", "br, gzip"));

            Assert.Equal(HttpStatusCode.OK, result.Status);
            Assert.Equal("brotli-bytes", result.Body);
            Assert.Equal("br", result.Response.Content.Headers.GetValues("Content-Encoding").Single());
            Assert.Contains("Accept-Encoding", string.Join(",", result.Response.Headers.Vary), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gzip_variant_is_used_when_brotli_is_not_accepted()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "style.css"), "original");
            await Task.Delay(10);
            await File.WriteAllTextAsync(Path.Combine(root, "style.css.gz"), "gzip-bytes");

            await using var server = await StartAsync(root, s => s.Root = root);
            using var client = server.CreateClient();

            var result = await SendAsync(client, "/style.css", ("Accept-Encoding", "gzip"));

            Assert.Equal("gzip-bytes", result.Body);
            Assert.Equal("gzip", result.Response.Content.Headers.GetValues("Content-Encoding").Single());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Identity_is_served_without_accept_encoding_or_for_ranges()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.js"), "original");
            await Task.Delay(10);
            await File.WriteAllTextAsync(Path.Combine(root, "app.js.br"), "brotli-bytes");

            await using var server = await StartAsync(root, s => s.Root = root);
            using var client = server.CreateClient();

            var plain = await SendAsync(client, "/app.js");
            Assert.Equal("original", plain.Body);
            Assert.False(plain.Response.Content.Headers.Contains("Content-Encoding"));

            var ranged = await SendAsync(client, "/app.js", ("Accept-Encoding", "br"), ("Range", "bytes=0-3"));
            Assert.Equal(HttpStatusCode.PartialContent, ranged.Status);
            Assert.Equal("orig", ranged.Body);
            Assert.False(ranged.Response.Content.Headers.Contains("Content-Encoding"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Fingerprinted_assets_are_immutable()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "app.4f3a9c1d.js"), "console.log(1)");

            await using var server = await StartAsync(root, s =>
            {
                s.Root = root;
                s.MaxAge = TimeSpan.FromMinutes(5);
            });
            using var client = server.CreateClient();

            var result = await SendAsync(client, "/app.4f3a9c1d.js");

            var cacheControl = result.Response.Headers.GetValues("Cache-Control").Single();
            Assert.Contains("immutable", cacheControl, StringComparison.Ordinal);
            Assert.Contains("max-age=31536000", cacheControl, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Spa_fallback_serves_index_for_extensionless_paths()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<html>app</html>");

            await using var server = await StartAsync(root, s =>
            {
                s.Root = root;
                s.SpaFallback = "index.html";
            });
            using var client = server.CreateClient();

            var result = await SendAsync(
                client,
                "/dashboard/reports",
                ("Accept", "text/html,application/xhtml+xml"));

            Assert.Equal(HttpStatusCode.OK, result.Status);
            Assert.Contains("app", result.Body, StringComparison.Ordinal);
            Assert.Equal("no-cache", result.Response.Headers.GetValues("Cache-Control").Single());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Spa_fallback_does_not_mask_missing_assets()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "<html>app</html>");

            await using var server = await StartAsync(root, s =>
            {
                s.Root = root;
                s.SpaFallback = "index.html";
            });
            using var client = server.CreateClient();

            var result = await SendAsync(client, "/missing.js", ("Accept", "text/html"));

            Assert.Equal(HttpStatusCode.NotFound, result.Status);
            Assert.DoesNotContain("app", result.Body, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Precompression_can_be_disabled()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "data.json"), "{\"a\":1}");
            await Task.Delay(10);
            await File.WriteAllTextAsync(Path.Combine(root, "data.json.br"), "compressed");

            await using var server = await StartAsync(root, s =>
            {
                s.Root = root;
                s.EnablePrecompressedFiles = false;
            });
            using var client = server.CreateClient();

            var result = await SendAsync(client, "/data.json", ("Accept-Encoding", "br"));

            Assert.Equal("{\"a\":1}", result.Body);
            Assert.False(result.Response.Content.Headers.Contains("Content-Encoding"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
