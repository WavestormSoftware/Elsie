using System.Text;
using Elsie.Web.Http;
using Xunit;

namespace Elsie.Web.Tests;

public class Http1ParserTests
{
    [Fact]
    public async Task Parses_simple_get()
    {
        var raw =
            "GET /hello?x=1&y=2 HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "User-Agent: test\r\n" +
            "\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream);
        var req = await reader.ReadAsync(CancellationToken.None);
        Assert.NotNull(req);
        Assert.Equal("GET", req!.Method);
        Assert.Equal("/hello", req.Path);
        Assert.Equal("?x=1&y=2", req.QueryString);
        Assert.Equal("1", req.QueryValues["x"][0]);
        Assert.Equal("2", req.QueryValues["y"][0]);
        Assert.True(req.KeepAlive);
        reader.DisposeBuffer();
    }

    [Fact]
    public async Task Parses_post_with_body()
    {
        var body = "{\"a\":1}";
        var raw =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            body;
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream);
        var req = await reader.ReadAsync(CancellationToken.None);
        Assert.NotNull(req);
        Assert.Equal("POST", req!.Method);
        Assert.Equal(body.Length, req.ContentLength);
        using var sr = new StreamReader(req.Body);
        Assert.Equal(body, await sr.ReadToEndAsync());
        Assert.False(req.KeepAlive);
        reader.DisposeBuffer();
    }

    [Fact]
    public async Task Rejects_body_over_max()
    {
        var body = new string('x', 100);
        var raw =
            "POST /echo HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "\r\n" +
            body;
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream, maxBodyBytes: 10);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));
        reader.DisposeBuffer();
    }

    [Fact]
    public async Task Rejects_oversized_headers()
    {
        var raw =
            "GET / HTTP/1.1\r\n" +
            "Host: localhost\r\n" +
            "X-Big: " + new string('Z', 5000) + "\r\n" +
            "\r\n";
        await using var stream = new MemoryStream(Encoding.ASCII.GetBytes(raw));
        var reader = new Http1RequestReader(stream, maxHeaderBytes: 200);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));
        reader.DisposeBuffer();
    }

    [Theory]
    [InlineData("?a=1&a=2", 2)]
    [InlineData("?q=hello+world", 1)]
    public void ParseQuery_multi_and_plus(string qs, int expectedCount)
    {
        var map = Http1RequestReader.ParseQuery(qs);
        if (qs.Contains("a=", StringComparison.Ordinal))
        {
            Assert.Equal(expectedCount, map["a"].Count);
        }
        else
        {
            Assert.Equal("hello world", map["q"][0]);
        }
    }

    [Fact]
    public async Task Eof_before_request_returns_null()
    {
        await using var stream = new MemoryStream();
        var reader = new Http1RequestReader(stream);
        var req = await reader.ReadAsync(CancellationToken.None);
        Assert.Null(req);
        reader.DisposeBuffer();
    }
}
