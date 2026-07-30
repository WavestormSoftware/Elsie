using System.Text;
using System.Text.Json;
using Xunit;

namespace Elsie.Tests;

public class ResultFactoryTests
{
    [Fact]
    public void Html_sets_content_type()
    {
        var r = ElsieResult.Html("<b>x</b>");
        Assert.Equal(200, r.StatusCode);
        Assert.Equal("text/html; charset=utf-8", r.ContentType);
        Assert.Equal("<b>x</b>", Encoding.UTF8.GetString(r.Body!.Value.Span));
    }

    [Fact]
    public void File_bytes_sets_disposition()
    {
        var r = ElsieResult.File("hi"u8.ToArray(), "text/plain", downloadName: "a.txt");
        Assert.Equal("text/plain", r.ContentType);
        Assert.Contains("a.txt", r.Headers["Content-Disposition"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_stream_copies_and_disposes()
    {
        var probe = new ProbeStream("data"u8.ToArray());
        var r = ElsieResult.File(probe, "application/octet-stream");
        var body = await r.BufferViaWriter();
        Assert.Equal("data"u8.ToArray(), body);
        Assert.True(probe.Disposed);
    }

    private sealed class ProbeStream : MemoryStream
    {
        public ProbeStream(byte[] buffer) : base(buffer) { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Created_and_Accepted()
    {
        var created = ElsieResult.Created("/items/1", new { id = 1 });
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("/items/1", created.Headers["Location"]);

        var accepted = ElsieResult.Accepted("/jobs/9");
        Assert.Equal(202, accepted.StatusCode);
        Assert.Equal("/jobs/9", accepted.Headers["Location"]);
    }

    [Fact]
    public void Redirect_variants()
    {
        Assert.Equal(302, ElsieResult.Redirect("/a").StatusCode);
        Assert.Equal(301, ElsieResult.Redirect("/a", permanent: true).StatusCode);
        Assert.Equal(307, ElsieResult.RedirectTemporary("/a").StatusCode);
        Assert.Equal(308, ElsieResult.RedirectPermanent("/a").StatusCode);
        Assert.Equal("/a", ElsieResult.RedirectTemporary("/a").Headers["Location"]);
    }

    [Fact]
    public void IfNoneMatch_and_NotModified()
    {
        var payload = ElsieResult.Text("body");
        var hit = ElsieResult.IfNoneMatch("\"abc\"", "\"abc\"", payload);
        Assert.Equal(304, hit.StatusCode);
        Assert.Equal("\"abc\"", hit.Headers["ETag"]);

        var miss = ElsieResult.IfNoneMatch("\"old\"", "\"abc\"", payload);
        Assert.Equal(200, miss.StatusCode);
        Assert.Equal("\"abc\"", miss.Headers["ETag"]);
    }

    [Fact]
    public void WithHeaders_and_WithCookie()
    {
        var r = ElsieResult.Text("x")
            .WithHeaders(new Dictionary<string, string> { ["X-A"] = "1", ["X-B"] = "2" })
            .WithCookie("sid", "v", new ElsieCookieOptions { HttpOnly = true });

        Assert.Equal("1", r.Headers["X-A"]);
        Assert.Equal("2", r.Headers["X-B"]);
        Assert.Contains("sid=v", r.Headers.GetValues("Set-Cookie")[0], StringComparison.Ordinal);
        Assert.Contains("HttpOnly", r.Headers.GetValues("Set-Cookie")[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerSentEvents_writes_events()
    {
        var r = ElsieResult.ServerSentEvents(async (w, ct) =>
        {
            await w.WriteEventAsync("hello", eventName: "greet", id: "1", cancellationToken: ct);
        });
        Assert.Equal("text/event-stream", r.ContentType);
        var body = Encoding.UTF8.GetString(await r.BufferViaWriter());
        Assert.Contains("event: greet", body, StringComparison.Ordinal);
        Assert.Contains("data: hello", body, StringComparison.Ordinal);
        Assert.Contains("id: 1", body, StringComparison.Ordinal);
    }
}

file static class ResultTestExtensions
{
    public static async Task<byte[]> BufferViaWriter(this ElsieResult result)
    {
        if (result.Body is { } b) return b.ToArray();
        if (result.BodyWriter is null) return Array.Empty<byte>();
        await using var ms = new MemoryStream();
        await result.BodyWriter(ms, CancellationToken.None);
        return ms.ToArray();
    }
}
