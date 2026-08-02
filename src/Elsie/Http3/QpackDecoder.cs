using System.Text;
using Elsie.Web.Http2;

namespace Elsie.Web.Http3;

/// <summary>Raised when a QPACK header block cannot be decoded (protocol error → H3_QPACK_DECOMPRESSION_FAILED).</summary>
internal sealed class QpackException(string message) : Exception(message);

/// <summary>
/// RFC 9204 QPACK decoder. Supports the static table, literal field lines (with/without
/// name reference), and Huffman-coded strings. The peer is advertised a dynamic table
/// capacity of zero, so dynamic insertions are rejected (protocol error).
/// </summary>
internal sealed class QpackDecoder
{
    /// <summary>Decodes a QPACK header block into field lines.</summary>
    public List<(string Name, string Value)> DecodeHeaderBlock(ReadOnlySpan<byte> block)
    {
        var fields = new List<(string, string)>();
        var pos = 0;
        var required = ReadInteger(block, ref pos, 8, 0);
        if (required != 0)
        {
            throw new QpackException(
                $"QPACK header block requires {required} dynamic inserts but capacity is 0.");
        }

        // Delta Base (7-bit prefix). With no dynamic table inserts the base is zero.
        _ = ReadInteger(block, ref pos, 7, 0);

        while (pos < block.Length)
        {
            var b = block[pos];
            if ((b & 0x80) != 0)
            {
                // Indexed field line (static or dynamic).
                var index = ReadInteger(block, ref pos, 7, 0);
                fields.Add(StaticEntry(index));
            }
            else if ((b & 0xC0) == 0x40)
            {
                // Literal field line with name reference + incremental indexing → insert.
                throw new QpackException("QPACK dynamic table insertion is not supported (capacity 0).");
            }
            else if ((b & 0xE0) == 0x20)
            {
                // Literal field line with name reference (001Nxxxx).
                var nameIndex = ReadInteger(block, ref pos, 5, 0);
                var name = StaticEntry(nameIndex).Name;
                var value = ReadString(block, ref pos, prefixBits: 7);
                fields.Add((name, value));
            }
            else if ((b & 0xF0) == 0x00)
            {
                // Literal field line with literal name (0000NHxxxx).
                var name = ReadString(block, ref pos, prefixBits: 4);
                var value = ReadString(block, ref pos, prefixBits: 7);
                fields.Add((name, value));
            }
            else
            {
                throw new QpackException($"Unknown QPACK instruction byte 0x{b:X2}.");
            }
        }

        return fields;
    }

    /// <summary>Static table entry for a QPACK 0-based index (only static supported).</summary>
    internal static (string Name, string Value) StaticEntry(int index)
    {
        // QPACK index N == HPACK static table entry N + 1 (entry 0 of the HPACK table is unused).
        var entryIndex = index + 1;
        if (entryIndex < 1 || entryIndex >= HpackCodec.StaticTable.Length)
        {
            throw new QpackException($"QPACK static table index {index} is out of range.");
        }

        return HpackCodec.StaticTable[entryIndex];
    }

    /// <summary>Reads a prefix integer (RFC 9204 §4.1.1).</summary>
    private static int ReadInteger(ReadOnlySpan<byte> data, ref int pos, int prefixBits, int ignoredPrefix)
    {
        if (pos >= data.Length)
        {
            throw new QpackException("Truncated QPACK integer.");
        }

        var max = (1 << prefixBits) - 1;
        var value = data[pos] & max;
        pos++;
        if (value < max)
        {
            return value;
        }

        var shift = 0;
        while (pos < data.Length)
        {
            var cont = data[pos++];
            value += (cont & 0x7F) << shift;
            shift += 7;
            if ((cont & 0x80) == 0)
            {
                return value;
            }
        }

        throw new QpackException("Truncated QPACK integer.");
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos, int prefixBits)
    {
        if (pos >= data.Length)
        {
            throw new QpackException("Truncated QPACK string.");
        }

        var huffman = (data[pos] & 0x80) != 0;
        var length = ReadInteger(data, ref pos, prefixBits, 0);
        if (pos + length > data.Length)
        {
            throw new QpackException("Truncated QPACK string.");
        }

        var bytes = data.Slice(pos, length);
        pos += length;
        if (huffman)
        {
            return HpackHuffman.Decode(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
