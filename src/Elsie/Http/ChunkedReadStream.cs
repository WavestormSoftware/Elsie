using System.Globalization;

namespace Elsie.Web.Http;

/// <summary>
/// Streaming decoder for <c>Transfer-Encoding: chunked</c> request bodies.
/// Shares the connection leftover buffer with the request reader.
/// </summary>
internal sealed class ChunkedReadStream : Stream, IDrainableRequestBody
{
    private enum State
    {
        ChunkSize,
        ChunkData,
        ChunkCrlf,
        Trailers,
        Done
    }

    private readonly Http1ConnectionBuffer _input;
    private readonly long _maxBodyBytes;
    private readonly int _maxLineLength;
    private readonly int _maxTrailerBytes;
    private State _state = State.ChunkSize;
    private int _chunkRemaining;
    private long _totalRead;
    private int _trailerBytes;
    private bool _disposed;

    public ChunkedReadStream(
        Http1ConnectionBuffer input,
        long maxBodyBytes,
        int maxLineLength,
        int maxTrailerBytes)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _maxBodyBytes = maxBodyBytes > 0 ? maxBodyBytes : 10 * 1024 * 1024;
        _maxLineLength = maxLineLength > 0 ? maxLineLength : 8 * 1024;
        _maxTrailerBytes = maxTrailerBytes > 0 ? maxTrailerBytes : 32 * 1024;
    }

    public bool IsFullyConsumed => _state == State.Done;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _totalRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length == 0 || _state == State.Done)
        {
            return 0;
        }

        while (true)
        {
            switch (_state)
            {
                case State.ChunkSize:
                    await ReadChunkSizeAsync(cancellationToken).ConfigureAwait(false);
                    if (_state == State.Done)
                    {
                        return 0;
                    }

                    continue;

                case State.ChunkData:
                {
                    if (_chunkRemaining == 0)
                    {
                        _state = State.ChunkCrlf;
                        continue;
                    }

                    var toRead = Math.Min(buffer.Length, _chunkRemaining);
                    var n = await _input.ReadAsync(buffer[..toRead], cancellationToken, idleTimeout: true)
                        .ConfigureAwait(false);
                    if (n == 0)
                    {
                        throw new ElsieRequestException(400, "Unexpected EOF in chunk.");
                    }

                    _chunkRemaining -= n;
                    _totalRead += n;
                    if (_totalRead > _maxBodyBytes)
                    {
                        throw new ElsieRequestException(413, "Body too large.");
                    }

                    if (_chunkRemaining == 0)
                    {
                        _state = State.ChunkCrlf;
                    }

                    return n;
                }

                case State.ChunkCrlf:
                    await ExpectEmptyLineAsync(cancellationToken).ConfigureAwait(false);
                    _state = State.ChunkSize;
                    continue;

                case State.Trailers:
                    await ConsumeTrailersAsync(cancellationToken).ConfigureAwait(false);
                    _state = State.Done;
                    return 0;

                default:
                    return 0;
            }
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state == State.Done)
        {
            return;
        }

        var buffer = new byte[64 * 1024];
        while (_state != State.Done)
        {
            var n = await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0 && _state != State.Done)
            {
                // ReadAsync returns 0 only when Done
                break;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private async Task ReadChunkSizeAsync(CancellationToken cancellationToken)
    {
        var sizeLine = await _input.ReadLineAsync(_maxLineLength, cancellationToken, idleTimeout: true)
            .ConfigureAwait(false);
        if (sizeLine is null)
        {
            throw new ElsieRequestException(400, "Unexpected EOF in chunk size.");
        }

        var semi = sizeLine.IndexOf(';');
        var hex = semi >= 0 ? sizeLine[..semi] : sizeLine;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
        {
            throw new ElsieRequestException(400, "Invalid chunk size.");
        }

        if (_totalRead + size > _maxBodyBytes)
        {
            throw new ElsieRequestException(413, "Body too large.");
        }

        if (size == 0)
        {
            _state = State.Trailers;
            return;
        }

        _chunkRemaining = size;
        _state = State.ChunkData;
    }

    private async Task ExpectEmptyLineAsync(CancellationToken cancellationToken)
    {
        var line = await _input.ReadLineAsync(_maxLineLength, cancellationToken, idleTimeout: true)
            .ConfigureAwait(false);
        if (line is null)
        {
            throw new ElsieRequestException(400, "Unexpected EOF after chunk.");
        }

        if (line.Length != 0)
        {
            throw new ElsieRequestException(400, "Invalid chunk framing.");
        }
    }

    private async Task ConsumeTrailersAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var trailer = await _input.ReadLineAsync(_maxLineLength, cancellationToken, idleTimeout: true)
                .ConfigureAwait(false);
            if (trailer is null)
            {
                throw new ElsieRequestException(400, "Unexpected EOF in chunk trailers.");
            }

            if (trailer.Length == 0)
            {
                return;
            }

            // +2 for CRLF that was stripped from the line representation budget.
            _trailerBytes += trailer.Length + 2;
            if (_trailerBytes > _maxTrailerBytes)
            {
                throw new ElsieRequestException(400, "Chunk trailers too large.");
            }
        }
    }
}
