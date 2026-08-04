using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using Elsie.Soak.Soak;
using Elsie.Web.Http3;

namespace Elsie.Soak.Clients;

/// <summary>A parsed HTTP/3 response.</summary>
internal readonly record struct H3Response(string Status, byte[] Body)
{
    public string BodyAsText => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Raw QUIC client for HTTP/3 against the Elsie host. Every QUIC operation is threaded with a
/// cancellation token (an outer linked timeout per op), and the client options MUST advertise
/// inbound bidirectional/unidirectional stream credit or everything hangs (RFC 9114 requires
/// the server's control + QPACK streams to flow). QPACK uses the framework's own internal
/// codecs so request/response header blocks are standards-correct.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class RawH3Client : IAsyncDisposable
{
    private const long NoError = 0x0100;

    private readonly QuicConnection _connection;
    private readonly int _port;
    private readonly QpackEncoder _encoder = new(encoderStream: null);
    private readonly QpackDecoder _decoder = new(maxCapacity: 0, decoderStream: null);
    private readonly List<Task> _drains = [];
    private QuicStream? _controlStream;
    private long _streamSeq;
    private volatile bool _closed;

    private RawH3Client(QuicConnection connection, int port)
    {
        _connection = connection;
        _port = port;
    }

    /// <summary>Connects to the server's HTTP/3 (UDP) endpoint and opens the client control stream.</summary>
    public static async Task<RawH3Client> ConnectAsync(int port, CancellationToken ct)
    {
        using var t = ct.LinkTimeout(TimeSpan.FromSeconds(10));
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, port),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [SslApplicationProtocol.Http3],
                RemoteCertificateValidationCallback = static (_, _, _, _) => true
            },
            // Required: without inbound stream credit the server's control/QPACK streams stall
            // and the connection hangs (the RFC 9114 unidirectional-stream deadlock).
            MaxInboundBidirectionalStreams = 100,
            MaxInboundUnidirectionalStreams = 100,
            DefaultStreamErrorCode = NoError,
            DefaultCloseErrorCode = NoError
        };

        var connection = await QuicConnection.ConnectAsync(options, t.Token).ConfigureAwait(false);
        var client = new RawH3Client(connection, port);
        try
        {
            await client.OpenControlStreamAsync(t.Token).ConfigureAwait(false);
            client.StartInboundPump(ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The client control stream (RFC 9114 §6.2.1). Holds it open for the connection's life —
    /// closing it early is H3_CLOSED_CRITICAL_STREAM. Advertises QPACK dynamic-table capacity 0
    /// so the server encodes responses with static/literal-only field lines (deterministic for
    /// the client-side decoder).
    /// </summary>
    private async Task OpenControlStreamAsync(CancellationToken ct)
    {
        using var t = ct.LinkTimeout(TimeSpan.FromSeconds(10));
        var token = t.Token;
        var control = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, token).ConfigureAwait(false);
        _controlStream = control;
        // Control stream type (0x00) + SETTINGS frame advertising QPACK dynamic-table
        // capacity 0 (so the server encodes responses with static/literal-only field lines).
        // Written as ONE buffered chunk: back-to-back split WriteAsync calls on a client
        // unidirectional stream intermittently stall the peer's delivery of subsequent request
        // streams (see report — transport-edge behavior).
        var preamble = new byte[] { 0x00, 0x04, 0x02, 0x01, 0x00 };
        await control.WriteAsync(preamble, token).ConfigureAwait(false);
        await control.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>Accepts and drains the server's unidirectional streams (control + QPACK) so
    /// server flow control can never stall under the 100-stream credit.</summary>
    private void StartInboundPump(CancellationToken ct)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    while (!_closed && !ct.IsCancellationRequested)
                    {
                        var stream = await _connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                        var drain = DrainStreamAsync(stream, ct);
                        lock (_drains)
                        {
                            _drains.Add(drain);
                        }
                    }
                }
                catch (Exception) when (ct.IsCancellationRequested || _closed)
                {
                    // normal shutdown
                }
                catch (Exception)
                {
                    // connection error: individual requests already counted their own failures
                }
            },
            CancellationToken.None);
    }

    private static async Task DrainStreamAsync(QuicStream stream, CancellationToken ct)
    {
        await using var s = stream;
        try
        {
            var buffer = new byte[4096];
            while (await s.ReadAsync(buffer, ct).ConfigureAwait(false) > 0)
            {
                // consume (discard) server unidirectional-stream bytes
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // shutdown
        }
        catch (Exception)
        {
            // stream aborted by the peer — no action needed
        }
    }

    /// <summary>Sends one request (HEADERS [+ DATA]) and reads the full response.</summary>
    public async Task<H3Response> RequestAsync(
        string method,
        string path,
        ReadOnlyMemory<byte> body = default,
        CancellationToken ct = default)
    {
        using var t = ct.LinkTimeout(TimeSpan.FromSeconds(15));
        var token = t.Token;
        await using var request = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
        var streamId = Interlocked.Increment(ref _streamSeq);
        var fields = new List<(string Name, string Value)>
        {
            (":method", method),
            (":scheme", "https"),
            (":path", path),
            (":authority", $"127.0.0.1:{_port}")
        };
        var block = _encoder.EncodeFieldSection(fields, streamId);
        await Http3FrameWriter.WriteAsync(request, new Http3Frame(Http3FrameType.Headers, block), token).ConfigureAwait(false);
        await WriteBodyFramesAsync(request, body, token).ConfigureAwait(false);
        await request.FlushAsync(token).ConfigureAwait(false);
        request.CompleteWrites();
        return await ReadResponseAsync(request, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a request stream and immediately resets it (RST_STREAM) after the HEADERS — the
    /// server must recover without poisoning the connection.
    /// </summary>
    public async Task ResetStreamAsync(CancellationToken ct)
    {
        using var t = ct.LinkTimeout(TimeSpan.FromSeconds(10));
        var token = t.Token;
        await using var request = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, token).ConfigureAwait(false);
        var streamId = Interlocked.Increment(ref _streamSeq);
        var fields = new List<(string Name, string Value)>
        {
            (":method", "GET"),
            (":scheme", "https"),
            (":path", "/slow"),
            (":authority", $"127.0.0.1:{_port}")
        };
        var block = _encoder.EncodeFieldSection(fields, streamId);
        await Http3FrameWriter.WriteAsync(request, new Http3Frame(Http3FrameType.Headers, block), token).ConfigureAwait(false);
        await request.FlushAsync(token).ConfigureAwait(false);
        request.Abort(QuicAbortDirection.Write, NoError); // RST_STREAM
    }

    private static async Task WriteBodyFramesAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        if (body.IsEmpty)
        {
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, ReadOnlyMemory<byte>.Empty), token).ConfigureAwait(false);
            return;
        }

        var offset = 0;
        while (offset < body.Length)
        {
            var take = Math.Min(16 * 1024, body.Length - offset);
            await Http3FrameWriter.WriteAsync(stream, new Http3Frame(Http3FrameType.Data, body.Slice(offset, take)), token).ConfigureAwait(false);
            offset += take;
        }
    }

    private async Task<H3Response> ReadResponseAsync(QuicStream stream, CancellationToken token)
    {
        string? status = null;
        using var payload = new MemoryStream();
        while (true)
        {
            var frame = await Http3FrameReader.ReadAsync(stream, token).ConfigureAwait(false);
            if (frame is null)
            {
                break; // FIN
            }

            if (frame.Value.Type == Http3FrameType.Headers)
            {
                var result = _decoder.DecodeHeaderBlock(frame.Value.Payload.Span);
                if (status is null)
                {
                    status = result.Fields?.FirstOrDefault(static f => f.Item1 == ":status").Item2 ?? "0";
                }
            }
            else if (frame.Value.Type == Http3FrameType.Data)
            {
                payload.Write(frame.Value.Payload.Span);
            }
        }

        return new H3Response(status ?? "0", payload.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            t.CancelAfter(TimeSpan.FromSeconds(5));
            await _connection.CloseAsync(NoError, t.Token).ConfigureAwait(false);
        }
        catch
        {
            // connection already gone
        }

        if (_controlStream is not null)
        {
            try
            {
                await _controlStream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_drains.ToArray()).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
            // best effort
        }
    }
}