using System.Text;
using Elsie.Web.Http2;
using Elsie.Web.Http3;
using Xunit;

namespace Elsie.Web.Tests;

public class Http3CodecTests
{
    private static QpackDecoder NewDecoder(int capacity = 4096) => new(capacity, decoderStream: null);

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

    [Fact]
    public void Varint_over_int_max_returns_protocol_error_sentinel()
    {
        // 8-byte varint encoding 2^31 (exceeds int.MaxValue → -1, no OverflowException).
        Span<byte> buffer = stackalloc byte[8];
        var len = QuicVarInt.Write(buffer, 1L << 31);
        Assert.Equal(8, len);
        Assert.Equal(-1, QuicVarInt.Read(buffer[..len], out var consumed));
        Assert.Equal(len, consumed);
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
    //  QPACK static table (RFC 9204 Appendix A)
    // ================================================================

    [Fact]
    public void Static_table_rfc9204_indices()
    {
        Assert.Equal((":authority", ""), QpackStaticTable.Entry(0));
        Assert.Equal((":path", "/"), QpackStaticTable.Entry(1));
        Assert.Equal((":status", "200"), QpackStaticTable.Entry(25));
        Assert.Equal(("content-type", "text/html; charset=utf-8"), QpackStaticTable.Entry(52));
        Assert.Equal(99, QpackStaticTable.Entries.Length);
    }

    // ================================================================
    //  QPACK encoder / decoder roundtrips (RFC 9204 §4.5)
    // ================================================================

    [Fact]
    public void Encoder_produces_static_references()
    {
        // :status 200 is a full static match (QPACK index 25 → 1 T=1 index25).
        // content-type + value matches static entry 52 → indexed field line 0xF4.
        var encoder = new QpackEncoder(encoderStream: null);
        var block = encoder.EncodeResponse(200, [("content-type", "text/html; charset=utf-8")], streamId: 0);
        Assert.Equal(
            new byte[] { 0x00, 0x00, (byte)(0xC0 | 25), (byte)(0xC0 | 52) },
            block);
    }

    [Fact]
    public void Encoder_decoder_roundtrip_static_only()
    {
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder();

        var block = encoder.EncodeResponse(404, [("x-error", "nope"), ("content-type", "text/plain")], streamId: 1);
        var fields = decoder.DecodeHeaderBlock(block).Fields!;
        Assert.Equal(
            [(":status", "404"), ("x-error", "nope"), ("content-type", "text/plain")],
            fields);
    }

    [Fact]
    public void Trailer_roundtrip()
    {
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder();

        var headers = new List<(string, string)>
        {
            ("host", "www.example.org"),
            ("accept", "text/html"),
            ("x-custom-header", "custom-value-123")
        };
        var block = encoder.EncodeTrailers(headers, streamId: 2);
        Assert.Equal(headers, decoder.DecodeHeaderBlock(block).Fields);
    }

    [Fact]
    public async Task Dynamic_table_roundtrip_with_inserts()
    {
        // Peer advertises a 4096-byte dynamic table; the encoder inserts repeated fields and
        // references them; a second block re-references the dynamic entries.
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder(capacity: 4096);
        encoder.SetPeerMaxTableCapacity(4096);

        var firstHeaders = new List<(string, string)>
        {
            ("x-session", "abc-123"),
            ("accept-language", "en-US,en;q=0.9")
        };

        var block1 = encoder.EncodeResponse(200, firstHeaders, streamId: 1);
        var instructions1 = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        Assert.NotEmpty(instructions1); // Set Dynamic Table Capacity + Insert instructions

        decoder.ProcessEncoderStream(instructions1);
        var fields1 = decoder.DecodeHeaderBlock(block1).Fields!;
        Assert.Equal(
            [(":status", "200"), ("x-session", "abc-123"), ("accept-language", "en-US,en;q=0.9")],
            fields1);

        // Second response reuses the dynamic entries (no new inserts expected for a full match).
        var block2 = encoder.EncodeResponse(200, [("x-session", "abc-123")], streamId: 2);
        var instructions2 = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        Assert.Empty(instructions2); // full dynamic match → no inserts

        var fields2 = decoder.DecodeHeaderBlock(block2).Fields!;
        Assert.Equal([(":status", "200"), ("x-session", "abc-123")], fields2);
    }

    [Fact]
    public async Task Encoder_instruction_stream_has_no_spurious_capacity_zero_prefix()
    {
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder(capacity: 4096);
        encoder.SetPeerMaxTableCapacity(4096);

        var block = encoder.EncodeResponse(200, [("x-session", "abc-123")], streamId: 1);
        var instructions = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);

        // The instruction stream must open with Set Dynamic Table Capacity = 4096 — first
        // byte 0x20|31 = 0x3F. A spurious leading 0x20 (Set Capacity = 0) used to precede it.
        Assert.NotEmpty(instructions);
        Assert.Equal(0x3F, instructions[0]);

        decoder.ProcessEncoderStream(instructions);
        Assert.Equal(
            [(":status", "200"), ("x-session", "abc-123")],
            decoder.DecodeHeaderBlock(block).Fields);
    }

    [Fact]
    public async Task Capacity_shrink_evicts_referenced_entries_like_the_peer_decoder()
    {
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder(capacity: 4096);
        encoder.SetPeerMaxTableCapacity(4096);

        // Three ~1035-byte entries, all referenced by the first response (RefCount > 0 on
        // every insert until the peer sends a Section Acknowledgment).
        var a = new string('a', 1000);
        var b = new string('b', 1000);
        var c = new string('c', 1000);
        var block1 = encoder.EncodeResponse(200, [("x-a", a), ("x-b", b), ("x-c", c)], streamId: 1);
        var instructions1 = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        decoder.ProcessEncoderStream(instructions1);
        Assert.Equal(
            [(":status", "200"), ("x-a", a), ("x-b", b), ("x-c", c)],
            decoder.DecodeHeaderBlock(block1).Fields);

        // The peer reduces SETTINGS_QPACK_MAX_TABLE_CAPACITY mid-connection to 2048.
        encoder.SetPeerMaxTableCapacity(2048);

        // The next encode inserts a new entry, forcing the shrink. The encoder must evict
        // referenced entries unconditionally (the peer decoder evicts the same way after a
        // Set-Capacity instruction), keeping both tables in sync at 2048.
        var block2 = encoder.EncodeResponse(200, [("x-new", "v")], streamId: 2);
        var instructions2 = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        Assert.NotEmpty(instructions2);
        Assert.Equal(0x3F, instructions2[0]); // Set Dynamic Table Capacity = 2048
        Assert.Equal(0xE1, instructions2[1]);
        Assert.Equal(0x0F, instructions2[2]);
        decoder.ProcessEncoderStream(instructions2);
        Assert.Equal(
            [(":status", "200"), ("x-new", "v")],
            decoder.DecodeHeaderBlock(block2).Fields);

        // x-a was evicted on both sides: re-encoding it must not emit a stale dynamic
        // reference — the decoder rejects references to evicted entries.
        var block3 = encoder.EncodeResponse(200, [("x-a", a)], streamId: 3);
        var instructions3 = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        decoder.ProcessEncoderStream(instructions3);
        Assert.Equal(
            [(":status", "200"), ("x-a", a)],
            decoder.DecodeHeaderBlock(block3).Fields);
    }

    [Fact]
    public async Task Small_capacity_dynamic_table_roundtrips()
    {
        // Small table (64 bytes = 2 entries max) forces eviction; wireRIC modulo arithmetic
        // must still reconstruct the right required insert count.
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder(capacity: 64);
        encoder.SetPeerMaxTableCapacity(64);

        var block = encoder.EncodeResponse(200, [("x-a", "value-a"), ("x-b", "value-b")], streamId: 1);
        var instructions = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        decoder.ProcessEncoderStream(instructions);
        var fields = decoder.DecodeHeaderBlock(block).Fields!;
        Assert.Equal([(":status", "200"), ("x-a", "value-a"), ("x-b", "value-b")], fields);
    }

    [Fact]
    public async Task Capacity_zero_path_never_inserts()
    {
        var encoder = new QpackEncoder(encoderStream: null); // peer capacity stays 0
        var decoder = NewDecoder(capacity: 0);

        var block = encoder.EncodeResponse(200, [("x-custom", "hello world")], streamId: 1);
        var instructions = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        Assert.Empty(instructions); // no encoder instructions at capacity 0

        Assert.Equal(
            [(":status", "200"), ("x-custom", "hello world")],
            decoder.DecodeHeaderBlock(block).Fields);
    }

    [Fact]
    public async Task Blocked_stream_is_delivered_after_encoder_instructions()
    {
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder(capacity: 4096);
        encoder.SetPeerMaxTableCapacity(4096);

        var block = encoder.EncodeResponse(200, [("x-late", "arrives-later")], streamId: 5);
        // Decode before feeding the instructions → blocked.
        var result = decoder.DecodeHeaderBlock(block);
        Assert.True(result.IsBlocked);
        Assert.Equal(1, result.RequiredInsertCount);

        // Feed the encoder instructions; the wait must complete and decode must succeed.
        var instructions = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        decoder.ProcessEncoderStream(instructions);

        var wait = decoder.WaitUntilUnblockedAsync(result.RequiredInsertCount, CancellationToken.None);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        var unblocked = decoder.DecodeHeaderBlock(block);
        Assert.False(unblocked.IsBlocked);
        Assert.Equal(
            [(":status", "200"), ("x-late", "arrives-later")],
            unblocked.Fields);
    }

    [Fact]
    public async Task Decoder_emits_section_acknowledgment_and_insert_count_increment()
    {
        var encoder = new QpackEncoder(encoderStream: null);
        var decoder = NewDecoder(capacity: 4096);
        encoder.SetPeerMaxTableCapacity(4096);

        var block = encoder.EncodeResponse(200, [("x-ack", "yes")], streamId: 7);
        var instructions = await encoder.FlushEncoderInstructionsAsync(CancellationToken.None);
        decoder.ProcessEncoderStream(instructions);
        decoder.DecodeHeaderBlock(block);

        var decoderInstructions = await decoder.DrainDecoderInstructionsAsync(CancellationToken.None);
        Assert.NotEmpty(decoderInstructions);
        // Starts with an Insert Count Increment (00 + 6-bit) for the insert.
        Assert.Equal(0x00, decoderInstructions[0] & 0xC0);

        // Mark the section decoded → Section Acknowledgment (1 + 7-bit stream id).
        decoder.MarkSectionDecoded(7);
        var ack = await decoder.DrainDecoderInstructionsAsync(CancellationToken.None);
        Assert.NotEmpty(ack);
        Assert.Equal(0x80, ack[0] & 0x80);
        Assert.Equal(7, ack[0] & 0x7F);
    }

    [Fact]
    public void Decoder_rejects_dynamic_insert_when_capacity_zero()
    {
        var decoder = NewDecoder(capacity: 0);
        // Insert With Name Reference (static) on the encoder stream with capacity 0 → error.
        Assert.Throws<QpackException>(() =>
            decoder.ProcessEncoderStream([0xC0, 0x01, 0x61]));
    }

    [Fact]
    public void Decoder_rejects_required_insert_count_when_capacity_zero()
    {
        var decoder = NewDecoder(capacity: 0);
        var block = new byte[] { 0x01, 0x00 }; // required insert count 1
        Assert.Throws<QpackException>(() => decoder.DecodeHeaderBlock(block));
    }

    // ================================================================
    //  RFC 9204 Appendix B vectors
    // ================================================================

    [Fact]
    public void Rfc9204_b1_literal_with_name_reference()
    {
        var decoder = NewDecoder();
        var block = Convert.FromHexString("0000510b2f696e6465782e68746d6c");
        var fields = decoder.DecodeHeaderBlock(block).Fields!;
        Assert.Equal([(":path", "/index.html")], fields);
    }

    [Fact]
    public void Rfc9204_b2_dynamic_table_post_base_references()
    {
        var decoder = NewDecoder(capacity: 220);
        // Set Dynamic Table Capacity=220; Insert With Name Reference (:authority=www.example.com,
        // :path=/sample/path).
        decoder.ProcessEncoderStream(Convert.FromHexString(
            "3fbd01c00f7777772e6578616d706c652e636f6dc10c2f73616d706c652f70617468"));
        Assert.Equal(2, decoder.InsertCount);

        // Required Insert Count=2, Base=0; two Indexed Field Lines with Post-Base Index.
        var fields = decoder.DecodeHeaderBlock(Convert.FromHexString("03811011")).Fields!;
        Assert.Equal(
            [(":authority", "www.example.com"), (":path", "/sample/path")],
            fields);
    }

    [Fact]
    public void Rfc9204_b3_insert_with_literal_name()
    {
        var decoder = NewDecoder(capacity: 4096);
        decoder.ProcessEncoderStream(Convert.FromHexString("4a637573746f6d2d6b65790c637573746f6d2d76616c7565"));
        Assert.Equal(1, decoder.InsertCount);

        // The inserted entry must be referenceable by a dynamic index.
        var fields = decoder.DecodeHeaderBlock(Convert.FromHexString("020080")).Fields!;
        Assert.Equal([("custom-key", "custom-value")], fields);
    }

    [Fact]
    public void Rfc9204_b4_duplicate_and_relative_references()
    {
        var decoder = NewDecoder(capacity: 220);
        // Reproduce B.2 inserts then the Duplicate (relative index 2 → abs 0).
        decoder.ProcessEncoderStream(Convert.FromHexString(
            "3fbd01c00f7777772e6578616d706c652e636f6dc10c2f73616d706c652f70617468"));
        decoder.ProcessEncoderStream(Convert.FromHexString("4a637573746f6d2d6b65790c637573746f6d2d76616c7565"));
        decoder.ProcessEncoderStream(Convert.FromHexString("02"));
        Assert.Equal(4, decoder.InsertCount);

        // Required Insert Count=4, Base=4; indexed dynamic (abs=3), indexed static (1),
        // indexed dynamic (abs=2).
        var fields = decoder.DecodeHeaderBlock(Convert.FromHexString("050080c181")).Fields!;
        Assert.Equal(
            [(":authority", "www.example.com"), (":path", "/"), ("custom-key", "custom-value")],
            fields);
    }

    [Fact]
    public void Rfc9204_b5_dynamic_insert_eviction()
    {
        var decoder = NewDecoder(capacity: 220);
        decoder.ProcessEncoderStream(Convert.FromHexString(
            "3fbd01c00f7777772e6578616d706c652e636f6dc10c2f73616d706c652f70617468"));
        decoder.ProcessEncoderStream(Convert.FromHexString("4a637573746f6d2d6b65790c637573746f6d2d76616c7565"));
        decoder.ProcessEncoderStream(Convert.FromHexString("02"));
        // Insert With Name Reference, dynamic relative index 1 (abs = 4-1-1 = 2) → custom-key=custom-value2.
        decoder.ProcessEncoderStream(Convert.FromHexString(
            "810d637573746f6d2d76616c756532"));
        Assert.Equal(5, decoder.InsertCount);
    }

    [Fact]
    public void Rfc9204_b2_literal_name_huffman_flag_at_bit4()
    {
        // RFC 9204 §4.5.6: the literal-name string literal's H flag sits at bit 3 of the first
        // byte (0x08), NOT bit 7. "abc" Huffman-coded (RFC 7541: a=0x03/5b, b=0x23/6b, c=0x04/5b)
        // → bits 00011 100011 00100 = 0x1C 0x64.
        // First byte: 001 N=0 H=1 len=2 → 0x20 | 0x08 | 0x02 = 0x2A.
        var decoder = NewDecoder();
        var block = new byte[] { 0x00, 0x00, 0x2A, 0x1C, 0x64, 0x03, (byte)'x', (byte)'y', (byte)'z' };
        var fields = decoder.DecodeHeaderBlock(block).Fields!;
        Assert.Equal([("abc", "xyz")], fields);
    }

    [Fact]
    public void Decoder_distinguishes_indexed_post_base_from_literal_literal()
    {
        var decoder = NewDecoder(capacity: 4096);
        // Insert :authority=www.example.com at abs 0, then:
        // 02 80 → required=1, base=0 (sign=1, delta=0);
        // 10 → indexed post-base index 0 (abs = base + 0);
        // 22 6162 → literal-with-literal-name (001 N=0 H=0 len=2) "ab"; value "1".
        decoder.ProcessEncoderStream(Convert.FromHexString("c00f7777772e6578616d706c652e636f6d"));
        var fields = decoder.DecodeHeaderBlock(Convert.FromHexString("0280102261620131")).Fields!;
        Assert.Equal(
            [(":authority", "www.example.com"), ("ab", "1")],
            fields);
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

    [Fact]
    public void Decoder_handles_huffman_coded_value_strings()
    {
        var decoder = NewDecoder();
        // Literal with name reference (static index 1 → :path), Huffman value "www.example.com"
        // (RFC 7541 C.4.2: f1e3 c2e5 f23a 6ba0 ab90 f4ff, 12 bytes).
        var block = new byte[] { 0x00, 0x00, 0x51, 0x8C }
            .Concat(Convert.FromHexString("f1e3c2e5f23a6ba0ab90f4ff"))
            .ToArray();
        var fields = decoder.DecodeHeaderBlock(block).Fields!;
        Assert.Equal([(":path", "www.example.com")], fields);
    }

    // ================================================================
    //  Control streams (RFC 9114 §6.2)
    // ================================================================

    [Fact]
    public async Task Server_preamble_advertises_qpack_settings()
    {
        await using var ms = new MemoryStream();
        var options = new Elsie.Web.ElsieServerOptions { QpackMaxTableCapacity = 4096, QpackBlockedStreams = 100 };
        await Http3ControlStreams.WriteServerPreambleAsync(ms, options, CancellationToken.None);
        ms.Position = 0;

        var first = new byte[1];
        Assert.Equal(1, await ms.ReadAsync(first));
        Assert.Equal(0x00, first[0] & 0x3F); // unidirectional stream type 0 (control)

        var frame = await Http3FrameReader.ReadAsync(ms, CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(Http3FrameType.Settings, frame!.Value.Type);
        var payload = frame.Value.Payload.ToArray();
        Assert.Contains((byte)0x01, payload); // SETTINGS_QPACK_MAX_TABLE_CAPACITY
        Assert.Contains((byte)0x07, payload); // SETTINGS_QPACK_BLOCKED_STREAMS
        Assert.Contains((byte)0x08, payload); // SETTINGS_ENABLE_CONNECT_PROTOCOL
    }
}
