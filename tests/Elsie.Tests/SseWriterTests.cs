using System.Text;
using Xunit;

namespace Elsie.Tests;

public class SseWriterTests
{
    private static async Task<string> WriteAsync(Func<ElsieSseWriter, Task> write)
    {
        using var stream = new MemoryStream();
        var writer = new ElsieSseWriter(stream);
        await write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Event_fields_are_emitted_in_order()
    {
        var output = await WriteAsync(w => w.WriteEventAsync("hello", eventName: "greeting", id: "7", retryMs: 1500));

        Assert.Equal("id: 7\nevent: greeting\nretry: 1500\ndata: hello\n\n", output);
    }

    [Fact]
    public async Task Multi_line_data_is_split_into_data_lines()
    {
        var output = await WriteAsync(w => w.WriteEventAsync("a\nb\nc"));

        Assert.Equal("data: a\ndata: b\ndata: c\n\n", output);
    }

    [Fact]
    public async Task Event_record_overload_writes_the_same_wire_format()
    {
        var output = await WriteAsync(w => w.WriteEventAsync(new ElsieSseEvent("x", EventName: "tick", Id: "1")));

        Assert.Equal("id: 1\nevent: tick\ndata: x\n\n", output);
    }

    [Fact]
    public async Task Heartbeat_is_a_comment()
    {
        var output = await WriteAsync(w => w.WriteHeartbeatAsync());

        Assert.Equal(": heartbeat\n\n", output);
    }

    [Fact]
    public async Task Retry_directive_is_written()
    {
        var output = await WriteAsync(w => w.WriteRetryAsync(3000));

        Assert.Equal("retry: 3000\n\n", output);
    }

    [Fact]
    public void Last_event_id_reads_header_then_query()
    {
        var fromHeader = new ElsieRequest(
            "GET",
            "/events",
            headers: new Dictionary<string, string> { ["Last-Event-ID"] = "42" });
        Assert.Equal("42", ElsieSse.GetLastEventId(fromHeader));

        var fromQuery = new ElsieRequest(
            "GET",
            "/events",
            query: new Dictionary<string, string> { ["lastEventId"] = "43" });
        Assert.Equal("43", ElsieSse.GetLastEventId(fromQuery));

        Assert.Null(ElsieSse.GetLastEventId(new ElsieRequest("GET", "/events")));
    }

    [Fact]
    public void Accepts_event_stream_matches_event_source()
    {
        Assert.True(ElsieSse.AcceptsEventStream(new ElsieRequest("GET", "/e")));
        Assert.True(ElsieSse.AcceptsEventStream(new ElsieRequest(
            "GET",
            "/e",
            headers: new Dictionary<string, string> { ["Accept"] = "text/event-stream" })));
        Assert.False(ElsieSse.AcceptsEventStream(new ElsieRequest(
            "GET",
            "/e",
            headers: new Dictionary<string, string> { ["Accept"] = "application/json" })));
    }
}
