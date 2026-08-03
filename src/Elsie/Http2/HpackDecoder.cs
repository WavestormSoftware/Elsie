namespace Elsie.Web.Http2;

/// <summary>
/// Stateful HPACK (RFC 7541) decoder with a per-connection dynamic table. The old stateless
/// decode only resolved the static table, so any client that reused a keep-alive connection
/// and referenced dynamic-table entries (grpc-go, some HTTP/2 stacks) lost headers on the 2nd+
/// request — e.g. a missing content-type made gRPC calls return 415. One instance is shared by
/// all request streams on a connection; the client's SETTINGS_HEADER_TABLE_SIZE caps the table.
/// </summary>
internal sealed class HpackDecoder
{
    // Newest entries first, per RFC 7541 §2.3.3.
    private readonly List<(string Name, string Value)> _dynamic = new();
    private int _dynamicSize;
    private long _maxDynamicSize = 4096; // default SETTINGS_HEADER_TABLE_SIZE

    public static readonly (string Name, string Value)[] StaticTable = HpackCodec.StaticTable;

    /// <summary>Applies the peer's SETTINGS_HEADER_TABLE_SIZE (0x1).</summary>
    public void SetMaxDynamicTableSize(long maxSize)
    {
        _maxDynamicSize = maxSize >= 0 ? maxSize : 0;
        Evict();
    }

    /// <summary>
    /// Decodes one HPACK header block against the connection's dynamic table. Throws
    /// <see cref="InvalidDataException"/> on malformed blocks (hash/never-indexed are safe).
    /// </summary>
    public List<(string Name, string Value)> Decode(ReadOnlySpan<byte> block)
    {
        var headers = new List<(string, string)>();
        var i = 0;
        while (i < block.Length)
        {
            var b = block[i];
            switch (b & 0xE0)
            {
                case 0x20:
                    // Dynamic table size update — 001xxxxx
                    var (newSize, afterSize) = HpackCodec.ReadInteger(block, i, 5);
                    i = afterSize;
                    if (newSize > _maxDynamicSize)
                    {
                        throw new InvalidDataException("HPACK table size update exceeds the max.");
                    }

                    SetMaxDynamicTableSize(newSize);
                    continue;

                case 0x00: // literal without indexing (0000) / never indexed (0001) — 0000xxxx / 0001xxxx
                case 0x10:
                    {
                        int nameBits = (b & 0xF0) == 0x10 ? 4 : 4;
                        var (nameIndex, ni) = HpackCodec.ReadInteger(block, i, nameBits);
                        i = ni;
                        var (name, next1) = ReadName(nameIndex, block, i);
                        i = next1;
                        var (value, next2) = HpackCodec.ReadString(block, i);
                        i = next2;
                        headers.Add((name, value));
                        continue;
                    }
            }

            if ((b & 0x80) != 0)
            {
                // Indexed header field — 1xxxxxxx
                var (index, ni) = HpackCodec.ReadInteger(block, i, 7);
                i = ni;
                headers.Add(GetIndexed(index));
                continue;
            }

            if ((b & 0xC0) == 0x40)
            {
                // Literal with incremental indexing — 01xxxxxx
                var (nameIndex, ni) = HpackCodec.ReadInteger(block, i, 6);
                i = ni;
                var (name, next1) = ReadName(nameIndex, block, i);
                i = next1;
                var (value, next2) = HpackCodec.ReadString(block, i);
                i = next2;
                Insert(name, value);
                headers.Add((name, value));
                continue;
            }

            // Fallback: skip an unrecognized prefix (defensive).
            i++;
        }

        return headers;
    }

    /// <summary>Decodes a name from a static/dynamic index or a literal string.</summary>
    private (string Name, int Next) ReadName(int index, ReadOnlySpan<byte> block, int offset)
    {
        if (index == 0)
        {
            return HpackCodec.ReadString(block, offset);
        }

        return (GetIndexed(index).Name, offset);
    }

    /// <summary>Resolves a 1-based index against the static table then the dynamic table.</summary>
    private (string Name, string Value) GetIndexed(int index)
    {
        if (index > 0 && index < StaticTable.Length)
        {
            return StaticTable[index];
        }

        var dynamicIndex = index - (StaticTable.Length - 1) - 1;
        if (dynamicIndex >= 0 && dynamicIndex < _dynamic.Count)
        {
            return _dynamic[dynamicIndex];
        }

        throw new InvalidDataException($"HPACK index {index} is out of range (static {StaticTable.Length - 1}, dynamic {_dynamic.Count}).");
    }

    /// <summary>Inserts a literal-with-incremental-indexing entry (newest first), evicting per RFC 7541 §4.4.</summary>
    private void Insert(string name, string value)
    {
        var entrySize = 32 + name.Length + value.Length;
        if (entrySize > _maxDynamicSize)
        {
            // Entry larger than the table: empty the table and do not add it.
            _dynamic.Clear();
            _dynamicSize = 0;
            return;
        }

        _dynamic.Insert(0, (name, value));
        _dynamicSize += entrySize;
        Evict();
    }

    private void Evict()
    {
        while (_dynamicSize > _maxDynamicSize && _dynamic.Count > 0)
        {
            // Newest-first list, so the oldest is at the end.
            var last = _dynamic[^1];
            _dynamic.RemoveAt(_dynamic.Count - 1);
            _dynamicSize -= 32 + last.Name.Length + last.Value.Length;
        }
    }
}
