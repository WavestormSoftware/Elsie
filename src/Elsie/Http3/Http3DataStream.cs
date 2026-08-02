using System.Net.Quic;

namespace Elsie.Web.Http3;

/// <summary>
/// Stream adapter that turns writes into HTTP/3 DATA frames on a request stream, so
/// unknown-length <c>BodyWriter</c> responses (SSE, static files with a known
/// Content-Length, chunked writers) stream incrementally instead of being buffered.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class Http3DataStream : Stream
{
    private readonly QuicStream _stream;
    private readonly CancellationToken _cancellationToken;

    public Http3DataStream(QuicStream stream, CancellationToken cancellationToken)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken;
        await Http3FrameWriter.WriteAsync(
            _stream,
            new Http3Frame(Http3FrameType.Data, buffer.ToArray()),
            token).ConfigureAwait(false);
    }

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>No-op: the request stream's FIN is set when the stream is disposed by the
    /// caller after the response has been fully written.</summary>
    public Task FinishAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
