using System.Text;
using Elsie.Web.Http2;

namespace Elsie.Web.Http3;

/// <summary>
/// RFC 9204 QPACK encoder for response headers. Uses only the static table and
/// literals (never inserts into the dynamic table; the peer is advertised capacity 0).
/// </summary>
internal static class QpackEncoder
{
    /// <summary>Encodes a response header block: required insert count 0, delta base 0, then field lines.</summary>
    public static byte[] EncodeResponse(int statusCode, IEnumerable<(string Name, string Value)> headers)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x00); // required insert count = 0 (8-bit prefix)
        ms.WriteByte(0x00); // delta base = 0 (7-bit prefix)

        WriteField(ms, ":status", statusCode.ToString());
        foreach (var (name, value) in headers)
        {
            if (name.StartsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            WriteField(ms, name.ToLowerInvariant(), value);
        }

        return ms.ToArray();
    }

    /// <summary>Encodes a trailer block (no :status pseudo-header).</summary>
    public static byte[] EncodeTrailers(IEnumerable<(string Name, string Value)> trailers)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(0x00); // required insert count = 0
        ms.WriteByte(0x00); // delta base = 0

        foreach (var (name, value) in trailers)
        {
            if (name.StartsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            WriteField(ms, name.ToLowerInvariant(), value);
        }

        return ms.ToArray();
    }

    private static void WriteField(MemoryStream ms, string name, string value)
    {
        var fullIndex = StaticIndex(name, value);
        if (fullIndex >= 0)
        {
            // Indexed field line: 1xxxxxxx
            WriteInteger(ms, fullIndex, 7, 0x80);
            return;
        }

        var nameIndex = StaticIndex(name);
        if (nameIndex >= 0)
        {
            // Literal field line with name reference: 0010xxxx (N=0), 5-bit index.
            WriteInteger(ms, nameIndex, 5, 0x20);
            WriteString(ms, value);
            return;
        }

        // Literal field line with literal name: 00000H + 4-bit name length, then raw name bytes.
        WriteInteger(ms, name.Length, 4, 0x00);
        var nameBytes = Encoding.UTF8.GetBytes(name);
        ms.Write(nameBytes, 0, nameBytes.Length);
        WriteString(ms, value);
    }

    /// <summary>Static table index (QPACK 0-based) for a full name+value match, or -1.</summary>
    private static int StaticIndex(string name, string value)
    {
        var table = HpackCodec.StaticTable;
        for (var i = 1; i < table.Length; i++)
        {
            if (table[i].Name == name && table[i].Value == value)
            {
                return i - 1;
            }
        }

        return -1;
    }

    /// <summary>Static table index for a name-only match, or -1.</summary>
    private static int StaticIndex(string name)
    {
        var table = HpackCodec.StaticTable;
        for (var i = 1; i < table.Length; i++)
        {
            if (table[i].Name == name)
            {
                return i - 1;
            }
        }

        return -1;
    }

    private static void WriteInteger(MemoryStream ms, int value, int prefixBits, byte prefixPattern)
    {
        var max = (1 << prefixBits) - 1;
        if (value < max)
        {
            ms.WriteByte((byte)(prefixPattern | value));
            return;
        }

        ms.WriteByte((byte)(prefixPattern | max));
        value -= max;
        while (value >= 128)
        {
            ms.WriteByte((byte)((value % 128) + 128));
            value /= 128;
        }

        ms.WriteByte((byte)value);
    }

    private static void WriteString(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        WriteInteger(ms, bytes.Length, 7, 0x00); // Huffman bit clear
        ms.Write(bytes, 0, bytes.Length);
    }
}
