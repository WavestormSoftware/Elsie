using System.Net.Quic;

namespace Elsie.Web.Http3;

/// <summary>
/// Thread-safe writer for a server→client QPACK encoder stream (unidirectional type 0x02,
/// RFC 9114 §6.2.2). Opened lazily on first use; carries unframed encoder instructions.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class QpackEncoderStream
{
    private readonly QuicConnection _connection;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private QuicStream? _stream;

    public QpackEncoderStream(QuicConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
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
        var len = QuicVarInt.Write(type, (ulong)Http3UnidirectionalStreamType.QpackEncoder);
        await stream.WriteAsync(type[..len].ToArray(), cancellationToken).ConfigureAwait(false);
        return stream;
    }
}

/// <summary>
/// Thread-safe writer for a server→client QPACK decoder stream (unidirectional type 0x03,
/// RFC 9114 §6.2.2). Opened lazily on first use; carries unframed decoder instructions.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class QpackDecoderStream
{
    private readonly QuicConnection _connection;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private QuicStream? _stream;

    public QpackDecoderStream(QuicConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
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
        var len = QuicVarInt.Write(type, (ulong)Http3UnidirectionalStreamType.QpackDecoder);
        await stream.WriteAsync(type[..len].ToArray(), cancellationToken).ConfigureAwait(false);
        return stream;
    }
}
