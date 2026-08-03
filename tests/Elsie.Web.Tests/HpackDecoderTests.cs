using Elsie.Web.Http2;
using Xunit;

namespace Elsie.Web.Tests;

/// <summary>
/// Regression tests for the stateful HPACK (RFC 7541) decoder. A stateless decoder resolves only
/// the static table, so a client that reuses a keep-alive connection and references dynamic-table
/// entries on the 2nd+ request loses headers (e.g. a missing content-type turned gRPC calls into
/// 415). grpc-go (grpcurl) does exactly this; these tests pin the fix.
/// </summary>
public class HpackDecoderTests
{
    [Fact]
    public void Second_request_referencing_dynamic_entry_keeps_headers()
    {
        var decoder = new HpackDecoder();

        // Request 1: literal with incremental indexing (0x40, name index 0) for content-type.
        var block1 = BuildLiteral("content-type", "application/grpc");
        Assert.Equal(("content-type", "application/grpc"), Assert.Single(decoder.Decode(block1)));

        // Request 2: indexed header field referencing the dynamic entry just inserted.
        // Static table occupies protocol indexes 1..61; the first inserted dynamic entry is
        // protocol 62 → 0x80 | 62 = 0xBE.
        Assert.Equal(("content-type", "application/grpc"), Assert.Single(decoder.Decode(new byte[] { 0xBE })));
    }

    [Fact]
    public void Dynamic_table_size_update_evicts_oldest_entries()
    {
        var decoder = new HpackDecoder();
        decoder.Decode(BuildLiteral("x-one", new string('a', 2000)));
        decoder.Decode(BuildLiteral("x-two", new string('b', 2000)));

        // Shrink below the two entries' combined size → both evicted (a reference to protocol 62
        // now throws instead of resolving).
        decoder.SetMaxDynamicTableSize(10);
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[] { 0xBE }));
    }

    [Fact]
    public void Oversized_entry_empties_the_dynamic_table()
    {
        var decoder = new HpackDecoder();
        decoder.Decode(BuildLiteral("x-small", "v"));
        decoder.Decode(BuildLiteral("x-huge", new string('z', 10_000)));

        // The 10k entry exceeds the default 4096 cap → table emptied, entry not added; the
        // previously inserted "x-small" entry is gone too.
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[] { 0xBE }));
    }

    [Fact]
    public void Literal_without_indexing_does_not_populate_dynamic_table()
    {
        var decoder = new HpackDecoder();
        // 0x00 literal-without-indexing, name index 0, then literal name + value.
        using var ms = new MemoryStream();
        ms.WriteByte(0x00);
        ms.WriteByte((byte)"x-secret".Length);
        ms.Write(System.Text.Encoding.ASCII.GetBytes("x-secret"));
        ms.WriteByte((byte)"value".Length);
        ms.Write(System.Text.Encoding.ASCII.GetBytes("value"));
        decoder.Decode(ms.ToArray());

        // Nothing was inserted → protocol 62 is still out of range.
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[] { 0xBE }));
    }

    private static void WriteHpackInt7(MemoryStream ms, int value)
    {
        // 7-bit prefix integer (RFC 7541 §5.1): values < 128 fit one byte, larger use
        // continuation groups of 7 bits.
        if (value < 128)
        {
            ms.WriteByte((byte)value);
            return;
        }

        ms.WriteByte(0x7F);
        value -= 127;
        while (value >= 128)
        {
            ms.WriteByte((byte)((value % 128) + 128));
            value /= 128;
        }

        ms.WriteByte((byte)value);
    }

    private static byte[] BuildLiteral(string name, string value)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x40); // literal with incremental indexing, name index 0
        WriteHpackInt7(ms, name.Length);
        ms.Write(System.Text.Encoding.ASCII.GetBytes(name));
        WriteHpackInt7(ms, value.Length);
        ms.Write(System.Text.Encoding.ASCII.GetBytes(value));
        return ms.ToArray();
    }
}
