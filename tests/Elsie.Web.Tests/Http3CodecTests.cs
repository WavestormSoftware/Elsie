using System.Text;
using Elsie.Web.Http2;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

public class Http3CodecTests
{
    // ================================================================
    //  QUIC varints (RFC 9000 §16)
    // ================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(1_073_741_823)]
    [InlineData(1_073_741_824)]
    [InlineData(int.MaxValue)]
    public void Varint_roundtrips(int value)
    {
        Span<byte> buffer = stackalloc byte[8];
        var len = QuicVarInt.Write(buffer, (ulong)value);
        var decoded = QuicVarInt.Read(buffer[..len], out var consumed);
        Assert.Equal(len, consumed);
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void Varint_one_byte_prefix_bounds()
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.Equal(1, QuicVarInt.Write(buffer, 63));
        Assert.Equal(2, QuicVarInt.Write(buffer, 64));
    }

    // ================================================================
    //  HTTP/3 frames (RFC 9114 §7.2)
    // ================================================================

    [Fact]
    public void Frame_write_parse_roundtrip()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = new Http3Frame(Http3FrameType.Headers, payload);
        var buffer = new byte[frame.EncodedLength];
        frame.Write(buffer);

        var parsed = Http3Frame.Parse(buffer);
        Assert.Equal(Http3FrameType.Headers, parsed.Type);
        Assert.Equal(payload, parsed.Payload.ToArray());
    }

    [Fact]
    public async Task Frame_reader_writer_stream_roundtrip()
    {
        await using var ms = new MemoryStream();
        await Http3FrameWriter.WriteAsync(
            ms,
            new Http3Frame(Http3FrameType.Data, new byte[] { 9, 8, 7 }),
            CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3FrameReader.ReadAsync(ms, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(Http3FrameType.Data, frame!.Value.Type);
        Assert.Equal(new byte[] { 9, 8, 7 }, frame.Value.Payload.ToArray());
        Assert.Null(await Http3FrameReader.ReadAsync(ms, CancellationToken.None)); // EOF
    }

    // ================================================================
    //  QPACK (RFC 9204)
    // ================================================================

    [Fact]
    public void Encoder_produces_expected_static_block()
    {
        // :method GET + :status 200 are full static matches (indexed field lines);
        // content-type matches the static name only (literal with name reference).
        // EncodeResponse emits only :status (responses carry no request pseudo-headers).
        var block = QpackEncoder.EncodeResponse(200, [("content-type", "text/html; charset=utf-8")]);
        var expected = new byte[]
        {
            0x00, 0x00,          // required insert count, delta base
            0x87,                // indexed: :status 200 (QPACK static index 7)
            0x3E,                // literal with name ref (content-type = QPACK static index 30, N=0)
            0x18,                // value length 24 (not huffman)
        } // + "text/html; charset=utf-8"
        ;
        Assert.Equal(expected.Length + 24, block.Length);
        Assert.Equal(expected, block.AsSpan(0, expected.Length).ToArray());
        Assert.Equal(
            "text/html; charset=utf-8",
            Encoding.ASCII.GetString(block, expected.Length, 24));
    }

    [Fact]
    public void Decoder_handles_indexed_and_literal_static_fields()
    {
        var block = QpackEncoder.EncodeResponse(200, [("content-type", "text/html; charset=utf-8")]);
        var fields = new QpackDecoder().DecodeHeaderBlock(block);
        Assert.Equal(
            [(":status", "200"), ("content-type", "text/html; charset=utf-8")],
            fields);
    }

    [Fact]
    public void Decoder_roundtrips_pseudo_header_response()
    {
        var block = QpackEncoder.EncodeResponse(404, [("x-error", "nope")]);
        var fields = new QpackDecoder().DecodeHeaderBlock(block);
        Assert.Equal(
            [(":status", "404"), ("x-error", "nope")],
            fields);
    }

    [Fact]
    public void Request_headers_roundtrip_through_encoder_and_decoder()
    {
        // Regular headers only (trailer encoding skips pseudo-headers by design).
        var headers = new List<(string, string)>
        {
            ("host", "www.example.org"),
            ("accept", "text/html"),
            ("accept-encoding", "gzip, deflate"),
            ("x-custom-header", "custom-value-123")
        };

        var block = QpackEncoder.EncodeTrailers(headers);
        var fields = new QpackDecoder().DecodeHeaderBlock(block);
        Assert.Equal(headers, fields);
    }

    [Fact]
    public void Decoder_rejects_dynamic_table_insertion()
    {
        // 0x40 prefix = literal with name reference + incremental indexing (insert).
        var block = new byte[] { 0x00, 0x00, 0x40, 0x03, 0x61, 0x62, 0x63, 0x01, 0x78 };
        Assert.Throws<QpackException>(() => new QpackDecoder().DecodeHeaderBlock(block));
    }

    [Fact]
    public void Decoder_requires_zero_insert_count()
    {
        var block = new byte[] { 0x01, 0x00 }; // required insert count 1
        Assert.Throws<QpackException>(() => new QpackDecoder().DecodeHeaderBlock(block));
    }

    // ================================================================
    //  Huffman (RFC 7541 Appendix B — shared with HPACK)
    // ================================================================

    [Fact]
    public void Huffman_decodes_rfc7541_example()
    {
        // RFC 7541 C.4.2: huffman("www.example.com") =
        // f1e3 c2e5 f23a 6ba0 ab90 f4ff
        var encoded = Convert.FromHexString("f1e3c2e5f23a6ba0ab90f4ff");
        Assert.Equal("www.example.com", HpackHuffman.Decode(encoded));
    }

    // ================================================================
    //  Control streams (RFC 9114 §6.2)
    // ================================================================

    [Fact]
    public async Task Server_preamble_contains_settings_frame()
    {
        await using var ms = new MemoryStream();
        await Http3ControlStreams.WriteServerPreambleAsync(ms, CancellationToken.None);
        ms.Position = 0;

        var first = new byte[1];
        Assert.Equal(1, await ms.ReadAsync(first));
        Assert.Equal(0x00, first[0] & 0x3F); // unidirectional stream type 0 (control)

        var frame = await Http3FrameReader.ReadAsync(ms, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(Http3FrameType.Settings, frame!.Value.Type);
        Assert.True(frame.Value.Payload.Length >= 3, "SETTINGS must carry QPACK table capacity + blocked streams + max field size");
    }
}
