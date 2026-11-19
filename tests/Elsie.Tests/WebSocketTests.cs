using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Elsie.Tests;

public class WebSocketTests
{
    private sealed class DuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);

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

        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _input.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            Output.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Output.WriteAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static byte[] Frame(byte opcode, byte[] payload, bool fin = true, bool masked = true)
    {
        var mask = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        var frame = new List<byte> { (byte)((fin ? 0x80 : 0x00) | opcode) };
        frame.Add(masked ? (byte)(0x80 | payload.Length) : (byte)payload.Length);
        if (masked)
        {
            frame.AddRange(mask);
        }

        for (var i = 0; i < payload.Length; i++)
        {
            frame.Add(masked ? (byte)(payload[i] ^ mask[i % 4]) : payload[i]);
        }

        return frame.ToArray();
    }

    private static byte[] Text(string value, bool fin = true) =>
        Frame(0x1, Encoding.UTF8.GetBytes(value), fin);

    private static (byte Opcode, byte[] Payload) ReadServerFrame(MemoryStream output)
    {
        var bytes = output.ToArray();
        Assert.True(bytes.Length >= 2);
        var opcode = (byte)(bytes[0] & 0x0F);
        var length = bytes[1] & 0x7F;
        var offset = 2;
        if (length == 126)
        {
            length = (bytes[2] << 8) | bytes[3];
            offset = 4;
        }
        else if (length == 127)
        {
            length = (int)(((long)bytes[2] << 56) | ((long)bytes[3] << 48) | ((long)bytes[4] << 40) | ((long)bytes[5] << 32)
                | ((long)bytes[6] << 24) | ((long)bytes[7] << 16) | ((long)bytes[8] << 8) | bytes[9]);
            offset = 10;
        }

        return (opcode, bytes[offset..(offset + length)]);
    }

    private static int CloseCode(byte[] payload) => (payload[0] << 8) | payload[1];

    [Fact]
    public async Task Fragmented_text_message_is_assembled_with_interleaved_ping()
    {
        var input = Text("Hel", fin: false)
            .Concat(Frame(0x9, "p"u8.ToArray()))
            .Concat(Frame(0x0, "lo"u8.ToArray()))
            .ToArray();
        var stream = new DuplexStream(input);
        await using var ws = new ElsieWebSocket(stream);

        var message = await ws.ReceiveAsync();

        Assert.NotNull(message);
        Assert.True(message!.IsText);
        Assert.Equal("Hello", message.GetText());

        // The interleaved ping was answered with a pong carrying the same payload.
        var pong = ReadServerFrame(stream.Output);
        Assert.Equal(0xA, pong.Opcode);
        Assert.Equal("p", Encoding.UTF8.GetString(pong.Payload));
    }

    [Fact]
    public async Task Unmasked_client_frame_fails_with_protocol_error()
    {
        var stream = new DuplexStream(Frame(0x1, "hi"u8.ToArray(), masked: false));
        await using var ws = new ElsieWebSocket(stream);

        var error = await Assert.ThrowsAsync<ElsieWebSocketException>(() => ws.ReceiveAsync());

        Assert.Equal(WebSocketCloseStatus.ProtocolError, error.CloseStatus);
        var close = ReadServerFrame(stream.Output);
        Assert.Equal(0x8, close.Opcode);
        Assert.Equal(1002, CloseCode(close.Payload));
    }

    [Fact]
    public async Task Masking_can_be_relaxed_for_raw_clients()
    {
        var stream = new DuplexStream(Frame(0x1, "hi"u8.ToArray(), masked: false));
        await using var ws = new ElsieWebSocket(stream, options: new ElsieWebSocketOptions { RequireMaskedFrames = false });

        var message = await ws.ReceiveAsync();

        Assert.Equal("hi", message!.GetText());
    }

    [Fact]
    public async Task Peer_close_is_echoed_and_status_recorded()
    {
        var stream = new DuplexStream(Frame(0x8, [0x03, 0xE8]));
        await using var ws = new ElsieWebSocket(stream);

        var message = await ws.ReceiveAsync();

        Assert.Null(message);
        Assert.True(ws.IsClosed);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, ws.CloseStatus);
        var close = ReadServerFrame(stream.Output);
        Assert.Equal(0x8, close.Opcode);
        Assert.Equal(1000, CloseCode(close.Payload));
    }

    [Fact]
    public async Task Oversized_message_fails_with_1009()
    {
        var stream = new DuplexStream(Text("12345"));
        await using var ws = new ElsieWebSocket(stream, options: new ElsieWebSocketOptions { MaxMessageBytes = 4 });

        var error = await Assert.ThrowsAsync<ElsieWebSocketException>(() => ws.ReceiveAsync());

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, error.CloseStatus);
    }

    [Fact]
    public async Task Invalid_utf8_fails_with_1007()
    {
        var stream = new DuplexStream(Frame(0x1, [0xC3, 0x28]));
        await using var ws = new ElsieWebSocket(stream);

        var error = await Assert.ThrowsAsync<ElsieWebSocketException>(() => ws.ReceiveAsync());

        Assert.Equal(WebSocketCloseStatus.InvalidPayloadData, error.CloseStatus);
    }

    [Fact]
    public async Task Send_text_writes_an_unmasked_final_text_frame()
    {
        var stream = new DuplexStream([]);
        await using var ws = new ElsieWebSocket(stream);

        await ws.SendTextAsync("hi");

        var frame = ReadServerFrame(stream.Output);
        Assert.Equal(0x1, frame.Opcode);
        Assert.Equal("hi", Encoding.UTF8.GetString(frame.Payload));
        Assert.Equal(2, ws.BytesSent);
    }

    [Fact]
    public async Task Send_uses_extended_length_for_large_payloads()
    {
        var stream = new DuplexStream([]);
        await using var ws = new ElsieWebSocket(stream);
        var payload = new byte[300];

        await ws.SendBinaryAsync(payload);

        var bytes = stream.Output.ToArray();
        Assert.Equal(0x82, bytes[0]);
        Assert.Equal(126, bytes[1]);
        Assert.Equal(300, (bytes[2] << 8) | bytes[3]);
    }

    [Fact]
    public async Task Close_sends_status_and_reason()
    {
        var stream = new DuplexStream([]);
        await using var ws = new ElsieWebSocket(stream);

        await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "nope");
        await ws.CloseAsync(); // idempotent

        var close = ReadServerFrame(stream.Output);
        Assert.Equal(1008, CloseCode(close.Payload));
        Assert.Equal("nope", Encoding.UTF8.GetString(close.Payload, 2, close.Payload.Length - 2));
        Assert.True(ws.IsClosed);
    }

    [Fact]
    public async Task Typed_receive_rejects_wrong_message_type()
    {
        var stream = new DuplexStream(Text("text"));
        await using var ws = new ElsieWebSocket(stream);

        var error = await Assert.ThrowsAsync<ElsieWebSocketException>(() => ws.ReceiveBinaryAsync());

        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, error.CloseStatus);
    }

    [Fact]
    public async Task Subprotocol_is_exposed()
    {
        var stream = new DuplexStream([]);
        await using var ws = new ElsieWebSocket(stream, subprotocol: "chat.v2");

        Assert.Equal("chat.v2", ws.Subprotocol);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Oversized_ping_payload_is_rejected()
    {
        var stream = new DuplexStream([]);
        await using var ws = new ElsieWebSocket(stream);

        await Assert.ThrowsAsync<ArgumentException>(() => ws.PingAsync(new byte[126]));
    }
}
