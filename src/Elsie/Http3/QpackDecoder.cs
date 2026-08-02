using System.Text;
using Elsie.Web.Http2;

namespace Elsie.Web.Http3;

/// <summary>Raised when a QPACK block or instruction cannot be decoded (protocol error).</summary>
internal class QpackException(string message) : Exception(message);

/// <summary>Raised when an encoder-stream instruction is truncated (needs more bytes).</summary>
internal sealed class QpackIncompleteException : QpackException
{
    public QpackIncompleteException() : base("Incomplete QPACK encoder instruction.") { }
}

/// <summary>Result of decoding a header block; blocked when the required inserts have not arrived yet.</summary>
internal sealed class QpackDecodeResult
{
    public bool IsBlocked { get; init; }
    public long RequiredInsertCount { get; init; }
    public List<(string Name, string Value)>? Fields { get; init; }
    public bool HasDynamicReferences { get; init; }
}

/// <summary>
/// RFC 9204 QPACK decoder: maintains the dynamic table (bounded by the capacity this endpoint
/// advertises in SETTINGS_QPACK_MAX_TABLE_CAPACITY), processes the peer's encoder instructions
/// from the QPACK encoder stream, and decodes header blocks with static + dynamic references
/// (relative and post-base), blocked-stream handling, and the required decoder instructions
/// (Section Acknowledgment / Insert Count Increment / Stream Cancellation).
/// </summary>
internal sealed class QpackDecoder
{
    private readonly object _gate = new();
    private readonly int _maxCapacity;
    private readonly List<QpackTableEntry> _table = []; // oldest first
    private readonly List<byte> _pending = [];          // partial encoder-stream bytes
    private readonly List<byte> _pendingDecoderInstructions = [];
    private readonly HashSet<long> _pendingAckStreams = [];
    private TaskCompletionSource _unblocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _tableSize;
    private long _insertCount;
    private long _ackedInsertCount;

    /// <summary>Creates a decoder. <paramref name="decoderStream"/> is null when capacity is 0
    /// (no decoder instructions are ever emitted).</summary>
    public QpackDecoder(int maxCapacity, QpackStream? decoderStream)
    {
        _maxCapacity = Math.Max(0, maxCapacity);
        DecoderStream = decoderStream;
    }

    /// <summary>Maximum dynamic-table capacity we advertise (limits the peer encoder).</summary>
    public int MaxCapacity => _maxCapacity;

    /// <summary>Total inserts processed from the peer encoder stream.</summary>
    public long InsertCount
    {
        get
        {
            lock (_gate)
            {
                return _insertCount;
            }
        }
    }

    internal QpackStream? DecoderStream { get; }

    /// <summary>
    /// Waits until the decoder has processed at least <paramref name="required"/> inserts
    /// (blocked-stream wakeup). Re-check the condition after the wait returns.
    /// </summary>
    public Task WaitUntilUnblockedAsync(long required, CancellationToken cancellationToken)
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_insertCount >= required)
            {
                return Task.CompletedTask;
            }

            tcs = _unblocked;
        }

        return tcs.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Feeds bytes from the peer's QPACK encoder stream (an unframed instruction sequence).
    /// Throws <see cref="QpackException"/> for invalid instructions (QPACK_ENCODER_STREAM_ERROR).
    /// </summary>
    public void ProcessEncoderStream(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _pending.AddRange(data.ToArray());
            while (_pending.Count > 0)
            {
                try
                {
                    if (!TryParseInstruction())
                    {
                        break;
                    }
                }
                catch (QpackIncompleteException)
                {
                    break; // wait for more bytes
                }
            }
        }
    }

    /// <summary>Decodes a header block. Does not mutate the dynamic table.</summary>
    public QpackDecodeResult DecodeHeaderBlock(ReadOnlySpan<byte> block)
    {
        var pos = 0;
        var wireRic = ReadInteger(block, ref pos, 8, 0);
        var requiredInsertCount = ReconstructRequiredInsertCount(wireRic);

        if (pos >= block.Length)
        {
            throw new QpackException("Truncated QPACK field section prefix.");
        }

        var signBit = (block[pos] & 0x80) != 0;
        var deltaBase = ReadInteger(block, ref pos, 7, 0x80);
        if (signBit && requiredInsertCount <= deltaBase)
        {
            throw new QpackException("Invalid QPACK field section base (negative).");
        }

        var baseIndex = signBit ? requiredInsertCount - deltaBase - 1 : requiredInsertCount + deltaBase;

        lock (_gate)
        {
            if (requiredInsertCount > _insertCount)
            {
                // Blocked stream: the peer must deliver the missing inserts first.
                return new QpackDecodeResult
                {
                    IsBlocked = true,
                    RequiredInsertCount = requiredInsertCount
                };
            }
        }

        var fields = new List<(string, string)>();
        var hasDynamicReferences = false;
        while (pos < block.Length)
        {
            var b = block[pos];
            if ((b & 0x80) != 0)
            {
                // Indexed Field Line (RFC 9204 §4.5.2): 1 T index(6+)
                var isStatic = (b & 0x40) != 0;
                var index = ReadInteger(block, ref pos, 6, 0x80);
                fields.Add(isStatic
                    ? QpackStaticTable.Entry(index)
                    : DynamicEntry(baseIndex - index - 1));
                hasDynamicReferences |= !isStatic;
            }
            else if ((b & 0xF0) == 0x10)
            {
                // Indexed Field Line with Post-Base Index (§4.5.3): 0001 index(4+)
                var index = ReadInteger(block, ref pos, 4, 0x10);
                fields.Add(DynamicEntry(baseIndex + index));
                hasDynamicReferences = true;
            }
            else if ((b & 0xC0) == 0x40)
            {
                // Literal Field Line with Name Reference (§4.5.4): 01 N T index(4+)
                var isStatic = (b & 0x10) != 0;
                var index = ReadInteger(block, ref pos, 4, 0x40);
                var name = isStatic
                    ? QpackStaticTable.Entry(index).Name
                    : DynamicEntry(baseIndex - index - 1).Name;
                var value = ReadString(block, ref pos, 8);
                fields.Add((name, value));
                hasDynamicReferences |= !isStatic;
            }
            else if ((b & 0xF0) == 0x00)
            {
                // Literal Field Line with Post-Base Name Reference (§4.5.5): 0000 N index(3+)
                var index = ReadInteger(block, ref pos, 3, 0x00);
                var name = DynamicEntry(baseIndex + index).Name;
                var value = ReadString(block, ref pos, 8);
                fields.Add((name, value));
                hasDynamicReferences = true;
            }
            else if ((b & 0xE0) == 0x20)
            {
                // Literal Field Line with Literal Name (§4.5.6): 001 N H name(4-bit prefix string)
                var name = ReadString(block, ref pos, 4);
                var value = ReadString(block, ref pos, 8);
                fields.Add((name, value));
            }
            else
            {
                throw new QpackException($"Unknown QPACK field line prefix 0x{b:X2}.");
            }
        }

        return new QpackDecodeResult
        {
            Fields = fields,
            RequiredInsertCount = requiredInsertCount,
            HasDynamicReferences = hasDynamicReferences
        };
    }

    /// <summary>
    /// Emits a Section Acknowledgment (RFC 9204 §4.4.1) for a fully processed field section
    /// that referenced the dynamic table. One ack per stream covers the earliest unacknowledged
    /// dynamic section on that stream.
    /// </summary>
    public void MarkSectionDecoded(long streamId)
    {
        lock (_gate)
        {
            if (!_pendingAckStreams.Add(streamId))
            {
                return;
            }

            WriteInteger(_pendingDecoderInstructions, streamId, 7, 0x80);
        }
    }

    /// <summary>Emits a Stream Cancellation (RFC 9204 §4.4.2) when the peer abandons a request
    /// stream that referenced the dynamic table.</summary>
    public void MarkStreamCancelled(long streamId)
    {
        lock (_gate)
        {
            if (!_pendingAckStreams.Remove(streamId))
            {
                return;
            }

            WriteInteger(_pendingDecoderInstructions, streamId, 6, 0x40);
        }
    }

    /// <summary>Writes any queued decoder instructions to the decoder stream; returns them
    /// (for tests, when <see cref="DecoderStream"/> is null).</summary>
    public async Task<byte[]> DrainDecoderInstructionsAsync(CancellationToken cancellationToken)
    {
        byte[] instructions;
        lock (_gate)
        {
            if (_pendingDecoderInstructions.Count == 0)
            {
                return [];
            }

            instructions = [.. _pendingDecoderInstructions];
            _pendingDecoderInstructions.Clear();
        }

        if (DecoderStream is not null)
        {
#pragma warning disable CA1416 // QUIC is only reachable from the platform-guarded connection path
            await DecoderStream.WriteAsync(instructions, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1416
        }

        return instructions;
    }

    // ------------------------------------------------------------------
    //  Encoder instruction parsing (RFC 9204 §4.3)
    // ------------------------------------------------------------------

    /// <summary>Parses one encoder instruction from <see cref="_pending"/>; removes it on success.</summary>
    private bool TryParseInstruction()
    {
        var data = _pending;
        var b = data[0];

        if ((b & 0x80) != 0)
        {
            // Insert with Name Reference (§4.3.2): 1 T nameIndex(6+) value(8-bit string)
            var isStatic = (b & 0x40) != 0;
            var index = ReadInteger(data, 0, 6, 0x80, out var nameLen);
            var name = isStatic
                ? QpackStaticTable.Entry(index).Name
                : TableEntry(_insertCount - index - 1).Name;
            var value = ReadString(data, nameLen, 8, out var totalLen);
            InsertEntry(name, value);
            Consume(totalLen);
            return true;
        }

        if ((b & 0xC0) == 0x40)
        {
            // Insert with Literal Name (§4.3.3): 01 name(6-bit prefix string) value(8-bit string)
            var name = ReadString(data, 0, 6, out var afterName);
            var value = ReadString(data, afterName, 8, out var totalLen);
            InsertEntry(name, value);
            Consume(totalLen);
            return true;
        }

        if ((b & 0xE0) == 0x20)
        {
            // Set Dynamic Table Capacity (§4.3.1): 001 capacity(5+)
            var capacity = ReadInteger(data, 0, 5, 0x20, out var len);
            SetCapacity(capacity);
            Consume(len);
            return true;
        }

        // Duplicate (§4.3.4): 000 relativeIndex(5+)
        var relIndex = ReadInteger(data, 0, 5, 0x00, out var dupLen);
        var entry = TableEntry(_insertCount - relIndex - 1);
        InsertEntry(entry.Name, entry.Value);
        Consume(dupLen);
        return true;
    }

    private void Consume(int count) => _pending.RemoveRange(0, count);

    private void InsertEntry(string name, string value)
    {
        var entry = new QpackTableEntry(name, value, _insertCount);
        if (entry.Size > _maxCapacity)
        {
            throw new QpackException(
                $"QPACK encoder attempted to insert an entry larger than the dynamic table capacity ({_maxCapacity}).");
        }

        while (_tableSize + entry.Size > _maxCapacity)
        {
            if (_table.Count == 0)
            {
                throw new QpackException("QPACK encoder attempted an insertion that exceeds capacity.");
            }

            var oldest = _table[0];
            _table.RemoveAt(0);
            _tableSize -= oldest.Size;
        }

        _table.Add(entry);
        _tableSize += entry.Size;
        _insertCount++;
        OnInsert();
    }

    private void SetCapacity(int capacity)
    {
        if (capacity > _maxCapacity)
        {
            throw new QpackException(
                $"QPACK encoder set table capacity {capacity} above our maximum {_maxCapacity}.");
        }

        while (_tableSize > capacity && _table.Count > 0)
        {
            var oldest = _table[0];
            _table.RemoveAt(0);
            _tableSize -= oldest.Size;
        }
    }

    private void OnInsert()
    {
        var increment = _insertCount - _ackedInsertCount;
        if (increment <= 0)
        {
            return;
        }

        _ackedInsertCount = _insertCount;
        WriteInteger(_pendingDecoderInstructions, increment, 6, 0x00);

        // Wake blocked streams: the table state advanced.
        var old = _unblocked;
        _unblocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        old.TrySetResult();
    }

    private QpackTableEntry TableEntry(long absoluteIndex)
    {
        if (absoluteIndex < 0 || absoluteIndex >= _insertCount)
        {
            throw new QpackException(
                $"QPACK reference to missing dynamic table entry (absolute index {absoluteIndex}).");
        }

        // Entries are inserted newest-last: position from start =
        // tableCount - (insertCount - absoluteIndex).
        var pos = _table.Count - (int)(_insertCount - absoluteIndex);
        if (pos < 0 || pos >= _table.Count)
        {
            throw new QpackException(
                $"QPACK reference to evicted dynamic table entry (absolute index {absoluteIndex}).");
        }

        return _table[pos];
    }

    private (string Name, string Value) DynamicEntry(long absoluteIndex)
    {
        var entry = TableEntry(absoluteIndex);
        return (entry.Name, entry.Value);
    }

    // ------------------------------------------------------------------
    //  Prefix decoding (RFC 9204 §4.5.1)
    // ------------------------------------------------------------------

    private long ReconstructRequiredInsertCount(long wireRic)
    {
        if (wireRic == 0)
        {
            return 0;
        }

        var maxEntries = _maxCapacity / 32;
        if (maxEntries == 0)
        {
            throw new QpackException("QPACK field section requires dynamic inserts but capacity is 0.");
        }

        var fullRange = 2L * maxEntries;
        if (wireRic > fullRange)
        {
            throw new QpackException($"QPACK encoded insert count {wireRic} is out of range.");
        }

        lock (_gate)
        {
            var maxValue = _insertCount + maxEntries;
            var maxWrapped = (maxValue / fullRange) * fullRange;
            var req = maxWrapped + wireRic - 1;
            if (req > maxValue)
            {
                if (req <= fullRange)
                {
                    throw new QpackException("QPACK insert count reconstruction failed.");
                }

                req -= fullRange;
            }

            if (req == 0)
            {
                throw new QpackException("QPACK required insert count must not be zero when encoded nonzero.");
            }

            return req;
        }
    }

    // ------------------------------------------------------------------
    //  Primitives
    // ------------------------------------------------------------------

    private static int ReadInteger(ReadOnlySpan<byte> data, ref int pos, int prefixBits, byte prefixPattern)
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
            if (shift >= 28)
            {
                // Continuation run would overflow the int accumulator: malformed QPACK integer.
                throw new QpackException("QPACK integer exceeds the supported range.");
            }

            value += (cont & 0x7F) << shift;
            shift += 7;
            if ((cont & 0x80) == 0)
            {
                return value;
            }
        }

        throw new QpackException("Truncated QPACK integer.");
    }

    private static int ReadInteger(List<byte> data, int pos, int prefixBits, byte prefixPattern, out int consumed)
    {
        if (pos >= data.Count)
        {
            throw new QpackIncompleteException();
        }

        var max = (1 << prefixBits) - 1;
        var value = data[pos] & max;
        pos++;
        consumed = 1;
        if (value < max)
        {
            return value;
        }

        var shift = 0;
        while (pos < data.Count)
        {
            var cont = data[pos++];
            consumed++;
            if (shift >= 28)
            {
                // Continuation run would overflow the int accumulator: malformed QPACK integer.
                throw new QpackException("QPACK integer exceeds the supported range.");
            }

            value += (cont & 0x7F) << shift;
            shift += 7;
            if ((cont & 0x80) == 0)
            {
                return value;
            }
        }

        throw new QpackIncompleteException();
    }

    /// <summary>
    /// Reads an N-bit-prefix string literal (RFC 9204 §4.1.2): H flag at bit (N-1) of the
    /// first byte, length as an (N-1)-bit prefix integer, then the string bytes.
    /// </summary>
    private static string ReadString(ReadOnlySpan<byte> data, ref int pos, int prefixBits)
    {
        if (pos >= data.Length)
        {
            throw new QpackException("Truncated QPACK string.");
        }

        var hbit = 1 << (prefixBits - 1);
        var huffman = (data[pos] & hbit) != 0;
        var length = ReadInteger(data, ref pos, prefixBits - 1, 0);
        if (pos + length > data.Length)
        {
            throw new QpackException("Truncated QPACK string.");
        }

        var bytes = data.Slice(pos, length);
        pos += length;
        return DecodeString(bytes, huffman);
    }

    private static string ReadString(List<byte> data, int pos, int prefixBits, out int consumed)
    {
        if (pos >= data.Count)
        {
            throw new QpackIncompleteException();
        }

        var hbit = 1 << (prefixBits - 1);
        var huffman = (data[pos] & hbit) != 0;
        var length = ReadInteger(data, pos, prefixBits - 1, 0, out var lenLen);
        pos += lenLen;
        if (pos + length > data.Count)
        {
            throw new QpackIncompleteException();
        }

        var bytes = data.GetRange(pos, length);
        pos += length;
        consumed = pos;
        return DecodeString(bytes, huffman);
    }

    private static string DecodeString(List<byte> bytes, bool huffman) =>
        DecodeString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes), huffman);

    private static string DecodeString(ReadOnlySpan<byte> bytes, bool huffman)
    {
        if (huffman)
        {
            return HpackHuffman.Decode(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
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
}
