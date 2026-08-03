using System.Net.Quic;

namespace Elsie.Web.Http3;

/// <summary>
/// Thread-safe writer for a server→client QPACK encoder/decoder stream (unidirectional types
/// 0x02/0x03, RFC 9114 §6.2.2). Opened lazily on first use; carries unframed encoder/decoder
/// instructions. One class covers both directions — only the stream-type byte differs.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class QpackStream : IAsyncDisposable
{
    private readonly QuicConnection _connection;
    private readonly Http3UnidirectionalStreamType _streamType;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private QuicStream? _stream;

    public QpackStream(QuicConnection connection, Http3UnidirectionalStreamType streamType)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _streamType = streamType;
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stream ??= await OpenAsync(cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<QuicStream> OpenAsync(CancellationToken cancellationToken)
    {
        var stream = await _connection.OpenOutboundStreamAsync(
            QuicStreamType.Unidirectional,
            cancellationToken).ConfigureAwait(false);
        Span<byte> type = stackalloc byte[8];
        var len = QuicVarInt.Write(type, (ulong)_streamType);
        await stream.WriteAsync(type[..len].ToArray(), cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <summary>Releases the lazily-opened stream at connection shutdown.</summary>
    public async ValueTask DisposeAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }
}
