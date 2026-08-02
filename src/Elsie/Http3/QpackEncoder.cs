using System.Runtime.InteropServices;
using System.Text;
using Elsie.Web.Http2;

namespace Elsie.Web.Http3;

/// <summary>
/// RFC 9204 QPACK encoder with a real dynamic table. Inserts entries into the dynamic table
/// (up to the capacity the peer advertises in SETTINGS_QPACK_MAX_TABLE_CAPACITY), emits the
/// matching encoder instructions on the QPACK encoder stream, and encodes response field
/// sections with static + dynamic references (relative and post-base). Falls back to
/// static/literal-only encoding while the peer capacity is zero (default), keeping the
/// capacity-0 interop path intact.
/// </summary>
internal sealed class QpackEncoder
{
    private const int MaxEncoderCapacity = 64 * 1024;
    private static readonly string[] SensitiveFields = ["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key"];

    private readonly object _gate = new();
    private readonly List<QpackTableEntry> _table = []; // oldest first
    private readonly List<byte> _pendingInstructions = [];
    private readonly List<byte> _pendingIncoming = [];  // partial client decoder-stream bytes
    private readonly Dictionary<long, List<QpackTableEntry>> _streamRefs = [];
    private readonly QpackEncoderStream? _encoderStream;
    private int _tableSize;
    private int _capacity;
    private long _insertCount;
    private long _peerMaxTableCapacity;
    private long _knownReceivedCount;

    /// <summary>Creates an encoder. <paramref name="encoderStream"/> may be null — the encoder
    /// then never emits instructions (peer capacity stays 0).</summary>
    public QpackEncoder(QpackEncoderStream? encoderStream)
    {
        _encoderStream = encoderStream;
    }

    /// <summary>Applies the peer's advertised SETTINGS_QPACK_MAX_TABLE_CAPACITY.</summary>
    public void SetPeerMaxTableCapacity(long capacity)
    {
        lock (_gate)
        {
            _peerMaxTableCapacity = Math.Max(0, capacity);
        }
    }

    /// <summary>Encodes a response header block (writes <c>:status</c> first).</summary>
    public byte[] EncodeResponse(int statusCode, IEnumerable<(string Name, string Value)> headers, long streamId)
    {
        lock (_gate)
        {
            using var ms = new MemoryStream();
            var block = EncodeCore(ms, streamId, fields =>
            {
                fields.Add((":status", statusCode.ToString()));
                foreach (var (name, value) in headers)
                {
                    if (name.Length > 0 && name[0] == ':')
                    {
                        continue;
                    }

                    fields.Add((name.ToLowerInvariant(), value));
                }
            });

            return block;
        }
    }

    /// <summary>Encodes a trailer block (no pseudo-headers).</summary>
    public byte[] EncodeTrailers(IEnumerable<(string Name, string Value)> trailers, long streamId)
    {
        lock (_gate)
        {
            using var ms = new MemoryStream();
            var block = EncodeCore(ms, streamId, fields =>
            {
                foreach (var (name, value) in trailers)
                {
                    if (name.Length > 0 && name[0] == ':')
                    {
                        continue;
                    }

                    fields.Add((name.ToLowerInvariant(), value));
                }
            });

            return block;
        }
    }

    /// <summary>Encodes an arbitrary field section verbatim (test-client helper; preserves
    /// pseudo-headers and case).</summary>
    internal byte[] EncodeFieldSection(IEnumerable<(string Name, string Value)> fields, long streamId)
    {
        lock (_gate)
        {
            using var ms = new MemoryStream();
            return EncodeCore(ms, streamId, list =>
            {
                foreach (var (name, value) in fields)
                {
                    list.Add((name, value));
                }
            });
        }
    }

    /// <summary>
    /// Feeds bytes from the peer's QPACK decoder stream (Section Acknowledgment / Stream
    /// Cancellation / Insert Count Increment; RFC 9204 §4.4).
    /// </summary>
    public void ProcessDecoderStream(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _pendingIncoming.AddRange(data.ToArray());
            while (TryParseDecoderInstruction())
            {
            }
        }
    }

    /// <summary>Writes queued encoder instructions to the encoder stream; returns them
    /// (for tests, when no encoder stream is configured).</summary>
    public async Task<byte[]> FlushEncoderInstructionsAsync(CancellationToken cancellationToken)
    {
        byte[] pending;
        lock (_gate)
        {
            if (_pendingInstructions.Count == 0)
            {
                return [];
            }

            pending = [.. _pendingInstructions];
            _pendingInstructions.Clear();
        }

        if (_encoderStream is not null)
        {
#pragma warning disable CA1416 // QUIC is only reachable from the platform-guarded connection path
            await _encoderStream.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1416
        }

        return pending;
    }

    // ------------------------------------------------------------------
    //  Encoding
    // ------------------------------------------------------------------

    private byte[] EncodeCore(MemoryStream ms, long streamId, Action<List<(string, string)>> addFields)
    {
        var fields = new List<(string, string)>();
        addFields(fields);

        var baseIndex = _insertCount;
        long requiredInsertCount = 0;
        var referenced = new List<QpackTableEntry>();

        foreach (var (name, value) in fields)
        {
            WriteFieldLine(ms, baseIndex, ref requiredInsertCount, referenced, name, value);
        }

        if (referenced.Count > 0)
        {
            foreach (var entry in referenced)
            {
                entry.RefCount++;
            }

            if (!_streamRefs.TryGetValue(streamId, out var existing))
            {
                existing = [];
                _streamRefs[streamId] = existing;
            }

            existing.AddRange(referenced);
        }

        var prefix = WritePrefix(baseIndex, requiredInsertCount);
        var body = ms.ToArray();
        var result = new byte[prefix.Length + body.Length];
        prefix.CopyTo(result, 0);
        body.CopyTo(result, prefix.Length);
        return result;
    }

    private void WriteFieldLine(
        MemoryStream ms,
        long baseIndex,
        ref long requiredInsertCount,
        List<QpackTableEntry> referenced,
        string name,
        string value)
    {
        var staticIndex = QpackStaticTable.Find(name, value);
        if (staticIndex >= 0)
        {
            // Indexed field line, static: 1 T=1 index(6+)
            WriteInteger(ms, staticIndex, 6, 0xC0);
            return;
        }

        var dyn = FindDynamic(name, value);
        if (dyn is not null)
        {
            WriteDynamicReference(ms, dyn, baseIndex);
            referenced.Add(dyn);
            requiredInsertCount = Math.Max(requiredInsertCount, dyn.AbsoluteIndex + 1);
            return;
        }

        if (_peerMaxTableCapacity > 0 && ShouldIndex(name, value) && TryInsert(name, value, out var inserted))
        {
            WriteDynamicReference(ms, inserted, baseIndex);
            referenced.Add(inserted);
            requiredInsertCount = Math.Max(requiredInsertCount, inserted.AbsoluteIndex + 1);
            return;
        }

        // Literal field line.
        var nameStatic = QpackStaticTable.FindName(name);
        if (nameStatic >= 0)
        {
            // Literal with name reference, static: 01 N=0 T=1 nameIndex(4+)
            WriteInteger(ms, nameStatic, 4, 0x50);
            WriteString(ms, value);
            return;
        }

        var dynName = FindDynamicName(name);
        if (dynName is not null)
        {
            // Literal with dynamic name reference (relative or post-base).
            if (dynName.AbsoluteIndex < baseIndex)
            {
                WriteInteger(ms, baseIndex - dynName.AbsoluteIndex - 1, 4, 0x40);
            }
            else
            {
                WriteInteger(ms, dynName.AbsoluteIndex - baseIndex, 3, 0x00);
            }

            WriteString(ms, value);
            return;
        }

        // Literal with literal name: 001 N=0 H=0 name(4-bit prefix string)
        WriteStringWithPrefix(ms, name, prefixBits: 4, prefixPattern: 0x20);
        WriteString(ms, value);
    }

    private void WriteDynamicReference(MemoryStream ms, QpackTableEntry entry, long baseIndex)
    {
        if (entry.AbsoluteIndex < baseIndex)
        {
            // Indexed field line, dynamic: 1 T=0 relativeIndex(6+)
            WriteInteger(ms, baseIndex - entry.AbsoluteIndex - 1, 6, 0x80);
        }
        else
        {
            // Indexed field line with post-base index: 0001 postBaseIndex(4+)
            WriteInteger(ms, entry.AbsoluteIndex - baseIndex, 4, 0x10);
        }
    }

    private byte[] WritePrefix(long baseIndex, long requiredInsertCount)
    {
        using var ms = new MemoryStream();
        if (requiredInsertCount == 0)
        {
            ms.WriteByte(0x00); // required insert count
            ms.WriteByte(0x00); // sign 0, delta base 0
            return ms.ToArray();
        }

        var maxEntries = Math.Max(1, _peerMaxTableCapacity / 32);
        var fullRange = 2L * maxEntries;
        var wireRic = (requiredInsertCount % fullRange) + 1;
        WriteInteger(ms, wireRic, 8, 0x00);
        if (baseIndex >= requiredInsertCount)
        {
            WriteInteger(ms, baseIndex - requiredInsertCount, 7, 0x00);
        }
        else
        {
            WriteInteger(ms, requiredInsertCount - baseIndex - 1, 7, 0x80);
        }

        return ms.ToArray();
    }

    // ------------------------------------------------------------------
    //  Dynamic table
    // ------------------------------------------------------------------

    private bool ShouldIndex(string name, string value) =>
        value.Length <= 512 &&
        name.Length > 0 &&
        Array.IndexOf(SensitiveFields, name) < 0 &&
        (name.Length == 0 || name[0] != ':');

    private bool TryInsert(string name, string value, out QpackTableEntry inserted)
    {
        inserted = null!;
        if (_peerMaxTableCapacity <= 0)
        {
            return false;
        }

        var entry = new QpackTableEntry(name, value, _insertCount);
        if (entry.Size > _peerMaxTableCapacity)
        {
            return false;
        }

        EnsureCapacity();

        while (_tableSize + entry.Size > _capacity)
        {
            if (_table.Count == 0)
            {
                return false;
            }

            var oldest = _table[0];
            if (oldest.RefCount > 0 || oldest.AbsoluteIndex >= _knownReceivedCount)
            {
                return false; // not evictable — cannot insert
            }

            _table.RemoveAt(0);
            _tableSize -= oldest.Size;
        }

        EmitInsertInstruction(name, value);
        _table.Add(entry);
        _tableSize += entry.Size;
        _insertCount++;
        inserted = entry;
        return true;
    }

    private void EnsureCapacity()
    {
        var target = (int)Math.Min(_peerMaxTableCapacity, MaxEncoderCapacity);
        if (_capacity == target)
        {
            return;
        }

        _capacity = target;
        _pendingInstructions.Add(0x20);
        WriteInteger(_pendingInstructions, target, 5, 0x20);

        while (_tableSize > _capacity && _table.Count > 0)
        {
            var oldest = _table[0];
            if (oldest.RefCount > 0)
            {
                break; // cannot evict referenced entries — leave the surplus
            }

            _table.RemoveAt(0);
            _tableSize -= oldest.Size;
        }
    }

    private void EmitInsertInstruction(string name, string value)
    {
        var nameStatic = QpackStaticTable.FindName(name);
        if (nameStatic >= 0)
        {
            // Insert with name reference, static: 1 T=1 nameIndex(6+)
            WriteInteger(_pendingInstructions, nameStatic, 6, 0xC0);
            WriteString(_pendingInstructions, value);
            return;
        }

        var dynName = FindDynamicName(name);
        if (dynName is not null)
        {
            // Insert with name reference, dynamic: 1 T=0 relativeIndex(6+)
            WriteInteger(_pendingInstructions, _insertCount - dynName.AbsoluteIndex - 1, 6, 0x80);
            WriteString(_pendingInstructions, value);
            return;
        }

        // Insert with literal name: 01 name(6-bit prefix string) value(8-bit string)
        WriteStringWithPrefix(_pendingInstructions, name, prefixBits: 6, prefixPattern: 0x40);
        WriteString(_pendingInstructions, value);
    }

    private QpackTableEntry? FindDynamic(string name, string value)
    {
        for (var i = _table.Count - 1; i >= 0; i--)
        {
            if (_table[i].Name == name && _table[i].Value == value)
            {
                return _table[i];
            }
        }

        return null;
    }

    private QpackTableEntry? FindDynamicName(string name)
    {
        for (var i = _table.Count - 1; i >= 0; i--)
        {
            if (_table[i].Name == name)
            {
                return _table[i];
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    //  Decoder instruction parsing (from the peer's QPACK decoder stream)
    // ------------------------------------------------------------------

    private bool TryParseDecoderInstruction()
    {
        var data = _pendingIncoming;
        if (data.Count == 0)
        {
            return false;
        }

        var b = data[0];
        if ((b & 0x80) != 0)
        {
            // Section Acknowledgment (§4.4.1): 1 streamId(7+)
            if (!TryReadIncomingInteger(7, out var streamId, out var ackConsumed))
            {
                return false;
            }

            ReleaseStream(streamId);
            _pendingIncoming.RemoveRange(0, ackConsumed);
            return true;
        }

        if ((b & 0xC0) == 0x40)
        {
            // Stream Cancellation (§4.4.2): 01 streamId(6+)
            if (!TryReadIncomingInteger(6, out var streamId, out var cancelConsumed))
            {
                return false;
            }

            ReleaseStream(streamId);
            _pendingIncoming.RemoveRange(0, cancelConsumed);
            return true;
        }

        // Insert Count Increment (§4.4.3): 00 increment(6+)
        if (!TryReadIncomingInteger(6, out var increment, out var incConsumed))
        {
            return false;
        }

        if (increment > 0)
        {
            _knownReceivedCount = Math.Min(_insertCount, _knownReceivedCount + increment);
        }

        _pendingIncoming.RemoveRange(0, incConsumed);
        return true;
    }

    /// <summary>Reads a prefix integer from the start of <see cref="_pendingIncoming"/>. Returns
    /// false (without consuming) when the instruction is truncated.</summary>
    private bool TryReadIncomingInteger(int prefixBits, out long value, out int consumed)
    {
        var data = _pendingIncoming;
        var max = (1 << prefixBits) - 1;
        var v = (long)(data[0] & max);
        var pos = 1;
        if (v < max)
        {
            value = v;
            consumed = 1;
            return true;
        }

        var shift = 0;
        while (pos < data.Count)
        {
            var cont = data[pos++];
            v += (long)(cont & 0x7F) << shift;
            shift += 7;
            if ((cont & 0x80) == 0)
            {
                value = v;
                consumed = pos;
                return true;
            }
        }

        value = 0;
        consumed = 0;
        return false;
    }

    private void ReleaseStream(long streamId)
    {
        if (_streamRefs.Remove(streamId, out var entries))
        {
            foreach (var entry in entries)
            {
                entry.RefCount = Math.Max(0, entry.RefCount - 1);
            }
        }
    }

    // ------------------------------------------------------------------
    //  Primitives
    // ------------------------------------------------------------------

    private static void WriteInteger(MemoryStream ms, long value, int prefixBits, byte prefixPattern)
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

    private static void WriteInteger(List<byte> buffer, long value, int prefixBits, byte prefixPattern)
    {
        var max = (1 << prefixBits) - 1;
        if (value < max)
        {
            buffer.Add((byte)(prefixPattern | value));
            return;
        }

        buffer.Add((byte)(prefixPattern | max));
        value -= max;
        while (value >= 128)
        {
            buffer.Add((byte)((value % 128) + 128));
            value /= 128;
        }

        buffer.Add((byte)value);
    }

    /// <summary>Writes an 8-bit-prefix string literal (H=0).</summary>
    private static void WriteString(MemoryStream ms, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInteger(ms, bytes.Length, 7, 0x00);
        ms.Write(bytes);
    }

    private static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInteger(buffer, bytes.Length, 7, 0x00);
        buffer.AddRange(bytes);
    }

    /// <summary>
    /// Writes an N-bit-prefix string literal (RFC 9204 §4.1.2) combined with its preceding
    /// instruction prefix bits: H flag at bit (N-1), length in the low (N-1) bits.
    /// </summary>
    private void WriteStringWithPrefix(Stream ms, string value, int prefixBits, byte prefixPattern)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteStringWithPrefix(ms, bytes, prefixBits, prefixPattern);
    }

    private void WriteStringWithPrefix(List<byte> buffer, string value, int prefixBits, byte prefixPattern)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteStringWithPrefix(buffer, bytes, prefixBits, prefixPattern);
    }

    private static void WriteStringWithPrefix(Stream ms, byte[] bytes, int prefixBits, byte prefixPattern)
    {
        var lenBits = prefixBits - 1;
        var max = (1 << lenBits) - 1;
        if (bytes.Length < max)
        {
            ms.WriteByte((byte)(prefixPattern | bytes.Length));
        }
        else
        {
            ms.WriteByte((byte)(prefixPattern | max));
            var value = bytes.Length - max;
            while (value >= 128)
            {
                ms.WriteByte((byte)((value % 128) + 128));
                value /= 128;
            }

            ms.WriteByte((byte)value);
        }

        ms.Write(bytes);
    }

    private static void WriteStringWithPrefix(List<byte> buffer, byte[] bytes, int prefixBits, byte prefixPattern)
    {
        var lenBits = prefixBits - 1;
        var max = (1 << lenBits) - 1;
        if (bytes.Length < max)
        {
            buffer.Add((byte)(prefixPattern | bytes.Length));
        }
        else
        {
            buffer.Add((byte)(prefixPattern | max));
            var value = bytes.Length - max;
            while (value >= 128)
            {
                buffer.Add((byte)((value % 128) + 128));
                value /= 128;
            }

            buffer.Add((byte)value);
        }

        buffer.AddRange(bytes);
    }
}
