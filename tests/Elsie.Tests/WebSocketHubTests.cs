using System.Text;
using Elsie.Realtime;
using Xunit;

namespace Elsie.Tests;

public class WebSocketHubTests
{
    private sealed class SinkStream : Stream
    {
        public MemoryStream Output { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);

        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Output.WriteAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static (ElsieWebSocket Socket, SinkStream Stream) CreateSocket()
    {
        var stream = new SinkStream();
        return (new ElsieWebSocket(stream), stream);
    }

    [Fact]
    public async Task Broadcast_reaches_every_connection_in_the_room()
    {
        var hub = new ElsieWebSocketHub();
        var (a, aStream) = CreateSocket();
        var (b, bStream) = CreateSocket();
        await using var socketA = a;
        await using var socketB = b;
        using var leaseA = hub.Join("lobby", socketA);
        using var leaseB = hub.Join("lobby", socketB);

        var sent = await hub.BroadcastAsync("lobby", "hi");

        Assert.Equal(2, sent);
        Assert.Contains("hi", Encoding.UTF8.GetString(aStream.Output.ToArray()), StringComparison.Ordinal);
        Assert.Contains("hi", Encoding.UTF8.GetString(bStream.Output.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rooms_are_isolated()
    {
        var hub = new ElsieWebSocketHub();
        var (a, aStream) = CreateSocket();
        var (b, bStream) = CreateSocket();
        await using var socketA = a;
        await using var socketB = b;
        using var leaseA = hub.Join("room-a", socketA);
        using var leaseB = hub.Join("room-b", socketB);

        var sent = await hub.BroadcastAsync("room-a", "only-a");

        Assert.Equal(1, sent);
        Assert.Contains("only-a", Encoding.UTF8.GetString(aStream.Output.ToArray()), StringComparison.Ordinal);
        Assert.Equal(0, bStream.Output.Length);
    }

    [Fact]
    public async Task Leaving_the_room_stops_delivery()
    {
        var hub = new ElsieWebSocketHub();
        var (socket, stream) = CreateSocket();
        await using var ws = socket;
        var lease = hub.Join("lobby", ws);
        lease.Dispose();

        var sent = await hub.BroadcastAsync("lobby", "nobody");

        Assert.Equal(0, sent);
        Assert.Equal(0, stream.Output.Length);
        Assert.Empty(hub.Rooms);
    }

    [Fact]
    public async Task Closed_connections_are_dropped()
    {
        var hub = new ElsieWebSocketHub();
        var (socket, _) = CreateSocket();
        await using var ws = socket;
        var group = hub.GetOrCreate("lobby");
        group.Add(ws);
        await ws.CloseAsync();

        var sent = await group.BroadcastTextAsync("x");

        Assert.Equal(0, sent);
        Assert.Equal(0, group.Count);
    }

    [Fact]
    public async Task Broadcast_all_spans_rooms()
    {
        var hub = new ElsieWebSocketHub();
        var (a, _) = CreateSocket();
        var (b, _) = CreateSocket();
        await using var socketA = a;
        await using var socketB = b;
        using var leaseA = hub.Join("one", socketA);
        using var leaseB = hub.Join("two", socketB);

        Assert.Equal(2, await hub.BroadcastAllAsync("everyone"));
        Assert.Equal(2, hub.Rooms.Count);
    }

    [Fact]
    public void Join_is_idempotent_for_the_same_socket()
    {
        var hub = new ElsieWebSocketHub();
        var (socket, _) = CreateSocket();
        var group = hub.GetOrCreate("room");
        group.Add(socket);
        group.Add(socket);

        Assert.Equal(1, group.Count);
    }
}
