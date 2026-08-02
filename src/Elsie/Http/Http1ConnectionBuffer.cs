using System.Buffers;
using System.Text;

namespace Elsie.Web.Http;

/// <summary>Shared leftover buffer for HTTP/1.1 header + body framing on one connection.</summary>
internal sealed class Http1ConnectionBuffer
{
    private readonly Stream _stream;
    private readonly TimeSpan _bodyIdleTimeout;
    private byte[] _buffer;
    private int _offset;
    private int _count;

    public Http1ConnectionBuffer(Stream stream, int capacity, TimeSpan bodyIdleTimeout)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _bodyIdleTimeout = bodyIdleTimeout;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 1024));
    }

    public int Count => _count;

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = Array.Empty<byte>();
            _offset = 0;
            _count = 0;
        }
    }

    /// <summary>Copy up to <paramref name="max"/> leftover bytes out and advance.</summary>
    public byte[]? TakePrefix(int max)
    {
        if (_count <= 0 || max <= 0)
        {
            return null;
        }

        var take = Math.Min(_count, max);
        var prefix = new byte[take];
        _buffer.AsSpan(_offset, take).CopyTo(prefix);
        Advance(take);
        return prefix;
    }

    public int CopyTo(Span<byte> destination)
    {
        if (_count <= 0 || destination.Length == 0)
        {
            return 0;
        }

        var take = Math.Min(_count, destination.Length);
        _buffer.AsSpan(_offset, take).CopyTo(destination);
        Advance(take);
        return take;
    }

    public async ValueTask EnsureBufferedAsync(CancellationToken cancellationToken, bool idleTimeout)
    {
        if (_count > 0)
        {
            return;
        }

        _offset = 0;
        var n = await ReadSocketAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken, idleTimeout)
            .ConfigureAwait(false);
        _count = n;
    }

    /// <summary>
    /// Read raw socket bytes into <paramref name="destination"/> (does not use leftover buffer).
    /// Prefer <see cref="ReadAsync"/> for body framing so leftover is consumed first.
    /// </summary>
    public ValueTask<int> ReadSocketAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken,
        bool idleTimeout) =>
        ReadSocketCoreAsync(destination, cancellationToken, idleTimeout);

    public async ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken,
        bool idleTimeout)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        if (_count > 0)
        {
            return CopyTo(destination.Span);
        }

        return await ReadSocketCoreAsync(destination, cancellationToken, idleTimeout).ConfigureAwait(false);
    }

    public async Task<string?> ReadLineAsync(
        int maxLineLength,
        CancellationToken cancellationToken,
        bool idleTimeout)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            if (_count == 0)
            {
                var n = await ReadSocketCoreAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken, idleTimeout)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    return ms.Length == 0 ? null : Encoding.ASCII.GetString(ms.ToArray());
                }

                _offset = 0;
                _count = n;
            }

            var span = _buffer.AsSpan(_offset, _count);
            var crlf = span.IndexOf((byte)'\n');
            if (crlf >= 0)
            {
                var end = crlf;
                if (end > 0 && span[end - 1] == (byte)'\r')
                {
                    end--;
                }

                if (ms.Length + end > maxLineLength)
                {
                    throw new InvalidOperationException("Line too long.");
                }

                ms.Write(span[..end]);
                Advance(crlf + 1);
                return Encoding.ASCII.GetString(ms.ToArray());
            }

            if (ms.Length + span.Length > maxLineLength)
            {
                throw new InvalidOperationException("Line too long.");
            }

            ms.Write(span);
            _offset = 0;
            _count = 0;
        }
    }

    /// <summary>Peek/consume for header block assembly (no idle timeout).</summary>
    public ReadOnlySpan<byte> AvailableSpan => _buffer.AsSpan(_offset, _count);

    public async ValueTask<int> FillAsync(CancellationToken cancellationToken)
    {
        _offset = 0;
        var n = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken)
            .ConfigureAwait(false);
        _count = n;
        return n;
    }

    public void Advance(int bytes)
    {
        if (bytes < 0 || bytes > _count)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        _offset += bytes;
        _count -= bytes;
        if (_count == 0)
        {
            _offset = 0;
        }
    }

    public void ClearAvailable()
    {
        _offset = 0;
        _count = 0;
    }

    private async ValueTask<int> ReadSocketCoreAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken,
        bool idleTimeout)
    {
        if (!idleTimeout || _bodyIdleTimeout == Timeout.InfiniteTimeSpan)
        {
            return await _stream.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(_bodyIdleTimeout);
        try
        {
            return await _stream.ReadAsync(destination, idleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ElsieRequestException(408, "Request body read timed out.");
        }
    }
}
