namespace Elsie.Web.Http;

/// <summary>
/// Content-Length request body over the connection stream. Does not pre-buffer;
/// enforces max size and idle timeout. Call <see cref="DrainAsync"/> before keep-alive reuse
/// when the handler did not fully consume the body.
/// </summary>
internal sealed class ElsieRequestBodyStream : Stream
{
    private readonly Stream _network;
    private readonly long _contentLength;
    private readonly TimeSpan _idleTimeout;
    private byte[]? _prefix;
    private int _prefixOffset;
    private long _consumed;
    private bool _disposed;

    public ElsieRequestBodyStream(
        Stream network,
        long contentLength,
        TimeSpan idleTimeout,
        byte[]? prefix)
    {
        if (contentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }

        _network = network ?? throw new ArgumentNullException(nameof(network));
        _contentLength = contentLength;
        _idleTimeout = idleTimeout <= TimeSpan.Zero ? Timeout.InfiniteTimeSpan : idleTimeout;
        _prefix = prefix is { Length: > 0 } ? prefix : null;
        if (_prefix is not null && _prefix.Length > contentLength)
        {
            throw new ArgumentException("Prefix longer than Content-Length.", nameof(prefix));
        }
    }

    public long ContentLength => _contentLength;

    public long Remaining => _contentLength - _consumed;

    public bool IsFullyConsumed => _consumed >= _contentLength;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _contentLength;

    public override long Position
    {
        get => _consumed;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0 || _consumed >= _contentLength)
        {
            return 0;
        }

        var toRead = (int)Math.Min(buffer.Length, _contentLength - _consumed);
        var total = 0;

        if (_prefix is not null)
        {
            var available = _prefix.Length - _prefixOffset;
            if (available > 0)
            {
                var take = Math.Min(available, toRead);
                _prefix.AsSpan(_prefixOffset, take).CopyTo(buffer.Span);
                _prefixOffset += take;
                _consumed += take;
                total += take;
                toRead -= take;
                if (_prefixOffset >= _prefix.Length)
                {
                    _prefix = null;
                    _prefixOffset = 0;
                }
            }
        }

        while (toRead > 0)
        {
            var n = await ReadNetworkAsync(buffer.Slice(total, toRead), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new ElsieRequestException(400, "Unexpected EOF in body.");
            }

            total += n;
            _consumed += n;
            toRead -= n;
        }

        return total;
    }

    /// <summary>Read and discard remaining body bytes so the connection can be reused.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_consumed >= _contentLength)
        {
            return;
        }

        var buffer = new byte[Math.Min(64 * 1024, (int)Math.Min(int.MaxValue, Remaining))];
        while (_consumed < _contentLength)
        {
            var n = await ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                throw new ElsieRequestException(400, "Unexpected EOF while draining body.");
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        _prefix = null;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _disposed = true;
        _prefix = null;
        return ValueTask.CompletedTask;
    }

    private async ValueTask<int> ReadNetworkAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_idleTimeout == Timeout.InfiniteTimeSpan)
        {
            return await _network.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleCts.CancelAfter(_idleTimeout);
        try
        {
            return await _network.ReadAsync(destination, idleCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ElsieRequestException(408, "Request body read timed out.");
        }
    }
}
