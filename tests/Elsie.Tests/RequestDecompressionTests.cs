using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Elsie.RequestDecompression;
using Elsie.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.Tests;

public class RequestDecompressionTests
{
    private const string Hello = "hello world, hello world, hello world";

    private sealed class DecompModule : ElsieModule
    {
        public DecompModule()
        {
            Post("/echo", async (ctx, ct) =>
            {
                var text = await ctx.Request.ReadTextAsync(ct);
                return ElsieResult.Text(text);
            });

            Post("/bind-json", async (ctx, ct) =>
            {
                var bind = await ctx.BindJsonAsync<Person>(ct);
                return bind.IsSuccess ? ctx.Json(bind.Value) : bind.Error!;
            });

            Post("/bind-form", async (ctx, ct) =>
            {
                var bind = await ctx.BindFormAsync<Person>(ct);
                return bind.IsSuccess ? ElsieResult.Text($"{bind.Value!.Name}:{bind.Value.Age}") : bind.Error!;
            });
        }
    }

    private sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    private static ElsieInMemoryHost CreateHost(Action<ElsieRequestDecompressionOptions>? options = null) =>
        ElsieInMemoryHost.Create(s =>
        {
            s.AddElsieModule<DecompModule>();
            s.AddRequestDecompression(options);
        });

    private static byte[] Gzip(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] Brotli(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            br.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var def = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            def.Write(data);
        }

        return ms.ToArray();
    }

    /// <summary>Apply codings in the order listed (RFC 9110: listed order = order applied).</summary>
    private static byte[] StackEncode(byte[] data, params string[] encodings)
    {
        var current = data;
        foreach (var encoding in encodings)
        {
            current = encoding switch
            {
                "gzip" => Gzip(current),
                "br" => Brotli(current),
                "deflate" => Deflate(current),
                _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
            };
        }

        return current;
    }

    private static async Task<ElsieInMemoryResponse> SendEncodedAsync(
        ElsieInMemoryHost host,
        string path,
        byte[] body,
        string contentEncoding,
        string contentType = "text/plain")
    {
        await using var stream = new MemoryStream(body);
        return await host.SendAsync(
            "POST",
            path,
            body: stream,
            contentLength: body.Length,
            contentType: contentType,
            headers: new Dictionary<string, string> { ["Content-Encoding"] = contentEncoding });
    }

    [Fact]
    public async Task Gzip_roundtrip_is_decoded()
    {
        await using var host = CreateHost();
        var res = await SendEncodedAsync(host, "/echo", Gzip(Encoding.UTF8.GetBytes(Hello)), "gzip");
        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Gzip_case_insensitive_coding_is_decoded()
    {
        await using var host = CreateHost();
        var res = await SendEncodedAsync(host, "/echo", Gzip(Encoding.UTF8.GetBytes(Hello)), "GZip");
        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Brotli_roundtrip_is_decoded()
    {
        await using var host = CreateHost();
        var res = await SendEncodedAsync(host, "/echo", Brotli(Encoding.UTF8.GetBytes(Hello)), "br");
        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Deflate_roundtrip_is_decoded()
    {
        await using var host = CreateHost();
        var res = await SendEncodedAsync(host, "/echo", Deflate(Encoding.UTF8.GetBytes(Hello)), "deflate");
        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Unsupported_encoding_returns_415()
    {
        await using var host = CreateHost();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Hello));
        var res = await host.SendAsync(
            "POST",
            "/echo",
            body: stream,
            contentLength: stream.Length,
            contentType: "text/plain",
            headers: new Dictionary<string, string> { ["Content-Encoding"] = "zstd" });

        Assert.Equal(415, res.StatusCode);
        Assert.Contains("Unsupported Media Type", res.ReadAsString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_encoding_passes_through_untouched()
    {
        await using var host = CreateHost();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Hello));
        var res = await host.SendAsync(
            "POST",
            "/echo",
            body: stream,
            contentLength: stream.Length,
            contentType: "text/plain");

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Identity_encoding_passes_through_untouched()
    {
        await using var host = CreateHost();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Hello));
        var res = await host.SendAsync(
            "POST",
            "/echo",
            body: stream,
            contentLength: stream.Length,
            contentType: "text/plain",
            headers: new Dictionary<string, string> { ["Content-Encoding"] = "identity" });

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Stacked_gzip_then_brotli_is_decoded()
    {
        // Content-Encoding: gzip, br → gzip applied first, then br.
        await using var host = CreateHost();
        var body = StackEncode(Encoding.UTF8.GetBytes(Hello), "gzip", "br");
        var res = await SendEncodedAsync(host, "/echo", body, "gzip, br");

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Stacked_brotli_then_gzip_is_decoded()
    {
        // Content-Encoding: br, gzip → br applied first, then gzip.
        await using var host = CreateHost();
        var body = StackEncode(Encoding.UTF8.GetBytes(Hello), "br", "gzip");
        var res = await SendEncodedAsync(host, "/echo", body, "br, gzip");

        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task Decompressed_body_over_cap_returns_413_mid_stream()
    {
        await using var host = CreateHost(o => o.MaxDecompressedBodySize = 64);
        var payload = new string('x', 4096);
        var res = await SendEncodedAsync(host, "/echo", Gzip(Encoding.UTF8.GetBytes(payload)), "gzip");

        Assert.Equal(413, res.StatusCode);
        Assert.Contains("Payload Too Large", res.ReadAsString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decompressed_body_within_cap_is_accepted()
    {
        await using var host = CreateHost(o => o.MaxDecompressedBodySize = 1024);
        var res = await SendEncodedAsync(host, "/echo", Gzip(Encoding.UTF8.GetBytes(Hello)), "gzip");
        Assert.Equal(200, res.StatusCode);
        Assert.Equal(Hello, res.ReadAsString());
    }

    [Fact]
    public async Task BindJsonAsync_after_decompression()
    {
        await using var host = CreateHost();
        var json = Encoding.UTF8.GetBytes("{\"Name\":\"Ada\",\"Age\":36}");
        var res = await SendEncodedAsync(host, "/bind-json", Gzip(json), "gzip", "application/json");

        Assert.Equal(200, res.StatusCode);
        using var doc = JsonDocument.Parse(res.ReadAsString());
        Assert.Equal("Ada", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(36, doc.RootElement.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task BindJsonAsync_after_brotli_decompression()
    {
        await using var host = CreateHost();
        var json = Encoding.UTF8.GetBytes("{\"Name\":\"Grace\",\"Age\":42}");
        var res = await SendEncodedAsync(host, "/bind-json", Brotli(json), "br", "application/json");

        Assert.Equal(200, res.StatusCode);
        using var doc = JsonDocument.Parse(res.ReadAsString());
        Assert.Equal("Grace", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task BindFormAsync_after_decompression()
    {
        await using var host = CreateHost();
        var form = Encoding.UTF8.GetBytes("Name=Bob&Age=20");
        var res = await SendEncodedAsync(host, "/bind-form", Gzip(form), "gzip", "application/x-www-form-urlencoded");

        Assert.Equal(200, res.StatusCode);
        Assert.Equal("Bob:20", res.ReadAsString());
    }
}