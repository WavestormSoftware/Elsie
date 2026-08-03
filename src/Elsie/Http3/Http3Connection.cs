using System.Collections.Concurrent;
using System.Net.Quic;
using Elsie;
using Elsie.Web.Hosting;
using Elsie.Web.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsie.Web.Http3;

/// <summary>
/// HTTP/3 connection handler (RFC 9114): opens the server control stream, reads the client's
/// unidirectional streams (control + QPACK encoder/decoder), and serves bidirectional request
/// streams through the same <c>HostDispatch</c> as HTTP/1.1 / HTTP/2. Full QPACK (RFC 9204)
/// with dynamic tables: the client's encoder stream feeds the per-connection
/// <see cref="QpackDecoder"/>, blocked request streams are delivered when the encoder stream
/// unblocks them, and responses are encoded with a real dynamic table bounded by the client's
/// advertised capacity. WebSocket over HTTP/3 (RFC 9220 extended CONNECT) is handled on the
/// request stream, reusing <see cref="ElsieWebSocket"/> framing.
/// Only instantiated when <c>QuicListener.IsSupported</c> (libmsquic present).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class Http3Connection
{
    private readonly IServiceProvider _services;
    private readonly HostDispatch _dispatch;
    private readonly ElsieServerOptions _serverOptions;
    private readonly Action<string>? _log;
    private readonly ILogger _logger;
    // Bounded wait for in-flight stream tasks once the accept loop ends (shutdown / abort).
    private static readonly TimeSpan StreamTaskShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<long, Task> _streamTasks = new();
    private QuicConnection _connection = null!;
    private QpackDecoder _decoder = null!;
    private QpackEncoder _encoder = null!;
    private QpackStream _decoderStream = null!;
    private QpackStream _encoderStream = null!;
    private QuicStream? _serverControlStream;
    private int _blockedStreams;

    public Http3Connection(
        IServiceProvider services,
        HostDispatch dispatch,
        ElsieServerOptions serverOptions,
        Action<string>? log,
        ILoggerFactory loggerFactory)
    {
        _services = services;
        _dispatch = dispatch;
        _serverOptions = serverOptions ?? new ElsieServerOptions();
        _log = log;
        _logger = loggerFactory.CreateLogger("Elsie.Http3");
    }

    public async Task RunAsync(QuicConnection connection, CancellationToken cancellationToken)
    {
        _connection = connection;
        _decoderStream = new QpackStream(connection, Http3UnidirectionalStreamType.QpackDecoder);
        _decoder = new QpackDecoder(_serverOptions.QpackMaxTableCapacity, _decoderStream);
        _encoderStream = new QpackStream(connection, Http3UnidirectionalStreamType.QpackEncoder);
        _encoder = new QpackEncoder(_encoderStream);

        try
        {
            // Server control stream (unidirectional): type + SETTINGS. Owned by the connection
            // and kept open for its whole life — closing it early is H3_CLOSED_CRITICAL_STREAM
            // (RFC 9114 §6.2.1). Opened on a tracked background task: a peer that advertises
            // zero unidirectional stream credit must not block the inbound accept loop.
            TrackStreamTask(-1, OpenServerControlStreamAsync(cancellationToken));

            while (!cancellationToken.IsCancellationRequested)
            {
                QuicStream stream;
                try
                {
                    stream = await connection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (QuicException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (QuicException)
                {
                    // Connection aborted / shutting down.
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var s = stream;
                if (!s.CanWrite)
                {
                    // Unidirectional stream (control / QPACK encoder+decoder).
                    TrackStreamTask(s.Id, Http3ControlStreams.ReadClientUnidirectionalStreamAsync(
                        s, _decoder, _encoder, _connection, cancellationToken));
                    continue;
                }

                // Bidirectional request stream.
                TrackStreamTask(s.Id, HandleRequestStreamAsync(s, cancellationToken));
            }
        }
        finally
        {
            // Shutdown: give tracked stream tasks a bounded window to observe cancellation and
            // unwind, then release the connection-owned streams so nothing outlives the
            // connection (no orphaned readers/writers behind a disposed QuicConnection).
            await WaitForStreamTasksAsync().ConfigureAwait(false);
            if (_serverControlStream is not null)
            {
                await _serverControlStream.DisposeAsync().ConfigureAwait(false);
            }

            await _decoderStream.DisposeAsync().ConfigureAwait(false);
            await _encoderStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Opens the server control stream and writes the SETTINGS preamble. Stream-credit
    /// waits are expected here (the peer may grant unidirectional credit late); failures are
    /// swallowed — the accept loop carries on and shutdown disposes whatever was opened.</summary>
    private async Task OpenServerControlStreamAsync(CancellationToken cancellationToken)
    {
        QuicStream? stream = null;
        try
        {
            stream = await _connection.OpenOutboundStreamAsync(
                QuicStreamType.Unidirectional,
                cancellationToken).ConfigureAwait(false);
            _serverControlStream = stream;
            await Http3ControlStreams.WriteServerPreambleAsync(stream, _serverOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or ObjectDisposedException)
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                _serverControlStream = null;
            }
        }
    }

    /// <summary>Tracks a per-stream task so shutdown can await it instead of orphaning it.</summary>
    private void TrackStreamTask(long streamId, Task task)
    {
        _streamTasks[streamId] = task;
        _ = task.ContinueWith(
            static (t, state) =>
            {
                var (self, id) = ((Http3Connection, long))state!;
                self._streamTasks.TryRemove(id, out _);
            },
            (this, streamId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task WaitForStreamTasksAsync()
    {
        var tasks = _streamTasks.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(StreamTaskShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: individual tasks already logged their own failures; on timeout the
            // connection dispose below aborts whatever is left.
        }
    }

    private async Task HandleRequestStreamAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        await using var s = stream;
        try
        {
            // Body state is per-stream (never shared across concurrent request streams).
            var bodyStream = new QuicRequestBodyStream(
                stream,
                _serverOptions.MaxRequestBodyBytes,
                _serverOptions.RequestBodyIdleTimeout);

            // The request scope must span dispatch and response writing (the handler and
            // DI middleware resolve from RequestServices), mirroring the HTTP/2 path.
            await using var scope = _services.CreateAsyncScope();
            var request = await ReadRequestAsync(stream, bodyStream, scope.ServiceProvider, cancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var response = await _dispatch.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
            if (bodyStream.IsTooLarge)
            {
                response = HostDispatch.FromResult(ElsieResult.Problem(
                    413,
                    "Request Entity Too Large",
                    "Request body exceeds the configured maximum."));
            }

            if (response is null)
            {
                return;
            }

            if (IsExtendedConnect(request) && response.WebSocketHandler is null)
            {
                // RFC 9220 §3: unknown/unsupported :protocol on an extended CONNECT → 501.
                response = HostDispatch.FromResult(ElsieResult.Problem(
                    501,
                    "Not Implemented",
                    $"The extended CONNECT protocol '{request.GetHeader(":protocol")}' is not supported."));
            }

            if (response.WebSocketHandler is not null)
            {
                await HandleWebSocketUpgradeAsync(stream, request, response, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await WriteResponseAsync(stream, response, request.Method, cancellationToken).ConfigureAwait(false);
            }

            // Flush any queued Section Acknowledgments for decoded request field sections.
            await _decoder.DrainDecoderInstructionsAsync(CancellationToken.None).ConfigureAwait(false);
            _log?.Invoke($"H3 {request.Method} {request.Path} → {response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
            _decoder.MarkStreamCancelled(stream.Id);
            await DrainDecoderInstructionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Http3ProtocolException ex)
        {
            // RFC 9114 §8.1: framing violations are connection errors — close explicitly with
            // the matching code instead of letting the peer hang until the MsQuic inactivity
            // timeout.
            _logger.LogDebug(ex, "HTTP/3 request stream violated framing rules.");
            _decoder.MarkStreamCancelled(stream.Id);
            await DrainDecoderInstructionsAsync(CancellationToken.None).ConfigureAwait(false);
            await CloseWithErrorAsync(ex.ErrorCode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or QuicException)
        {
            // Client aborted the stream — nothing to write.
            _decoder.MarkStreamCancelled(stream.Id);
            await DrainDecoderInstructionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP/3 request stream failed.");
        }
    }

    /// <summary>Reads the HEADERS frame, decodes QPACK (blocking until unblocked), and builds an
    /// <see cref="ElsieRequest"/>. The request body is served lazily by the caller-started pump.
    /// Extended-CONNECT (RFC 9220) streams carry WebSocket data, not an HTTP body — the caller
    /// must NOT start the pump for them (it would race the WebSocket frame reader).</summary>
    private async Task<ElsieRequest?> ReadRequestAsync(
        QuicStream stream,
        QuicRequestBodyStream bodyStream,
        IServiceProvider requestServices,
        CancellationToken cancellationToken)
    {
        var request = await ReadRequestCoreAsync(stream, bodyStream, requestServices, cancellationToken)
            .ConfigureAwait(false);
        if (request is not null && !IsExtendedConnect(request))
        {
            bodyStream.StartReadingAsync(cancellationToken);
        }

        return request;
    }

    private static bool IsExtendedConnect(ElsieRequest request) =>
        string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase)
        && request.GetHeader(":protocol") is not null;

    private async Task<ElsieRequest?> ReadRequestCoreAsync(
        QuicStream stream,
        QuicRequestBodyStream bodyStream,
        IServiceProvider requestServices,
        CancellationToken cancellationToken)
    {
        // Request HEADERS frame (DATA body follows; served lazily).
        Http3Frame first;
        while (true)
        {
            var frame = await Http3FrameReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return null;
            }

            if (frame.Value.Type == Http3FrameType.Headers)
            {
                first = frame.Value;
                break;
            }

            if (Enum.IsDefined(frame.Value.Type))
            {
                // RFC 9114 §4.1: a known frame before the initial HEADERS is H3_FRAME_UNEXPECTED.
                throw new Http3ProtocolException(
                    $"HTTP/3 request stream started with frame type 0x{(long)frame.Value.Type:X} (HEADERS required).",
                    Http3ErrorCodes.FrameUnexpected);
            }
            // Unknown/extension frame types MUST be ignored (RFC 9114 §9).
        }

        var block = first.Payload;
        if (block.Length > Math.Max(0, _serverOptions.Http3MaxFieldSectionBytes))
        {
            // RFC 9114 §4.2.2: a field section larger than the advertised
            // SETTINGS_MAX_FIELD_SECTION_SIZE is an H3_EXCESSIVE_LOAD connection error.
            throw new Http3ProtocolException(
                $"HTTP/3 request field section ({block.Length} bytes) exceeds the advertised {Math.Max(0, _serverOptions.Http3MaxFieldSectionBytes)}-byte limit.",
                Http3ErrorCodes.ExcessiveLoad);
        }

        QpackDecodeResult result;
        try
        {
            result = _decoder.DecodeHeaderBlock(block.Span);
            if (result.IsBlocked)
            {
                // Blocked stream: wait for the encoder instruction stream to catch up.
                if (Interlocked.Increment(ref _blockedStreams) > Math.Max(0, _serverOptions.QpackBlockedStreams))
                {
                    Interlocked.Decrement(ref _blockedStreams);
                    throw new QpackException(
                        "Client exceeded SETTINGS_QPACK_BLOCKED_STREAMS (too many blocked HTTP/3 request streams).");
                }

                try
                {
                    while (result.IsBlocked)
                    {
                        await _decoder
                            .WaitUntilUnblockedAsync(result.RequiredInsertCount, cancellationToken)
                            .ConfigureAwait(false);
                        result = _decoder.DecodeHeaderBlock(block.Span);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _blockedStreams);
                }
            }
        }
        catch (QpackException)
        {
            // RFC 9114 §8.1: an undecodable field section (or exceeding the advertised blocked
            // stream budget) poisons the decoder state for every later stream — terminate the
            // connection with H3_QPACK_DECOMPRESSION_FAILED instead of failing stream-by-stream.
            await CloseWithErrorAsync(Http3QpackErrorCodes.DecompressionFailed, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (result.Fields is null)
        {
            return null;
        }

        var fields = result.Fields;
        string? method = null, path = null, scheme = null, authority = null, protocol = null;
        string? contentType = null;
        long? contentLength = null;
        var headerDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in fields)
        {
            if (name.StartsWith(':'))
            {
                switch (name)
                {
                    case ":method":
                        method = value;
                        break;
                    case ":path":
                        path = value;
                        break;
                    case ":scheme":
                        scheme = value;
                        break;
                    case ":authority":
                        authority = value;
                        break;
                    case ":protocol":
                        protocol = value;
                        break;
                }

                continue;
            }

            if (name is "connection" or "transfer-encoding" or "keep-alive" or "proxy-connection" or "upgrade")
            {
                return null;
            }

            if (!headerDict.TryGetValue(name, out var list))
            {
                list = [];
                headerDict[name] = list;
            }

            list.Add(value);

            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
            else if (string.Equals(name, "content-length", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(value, out var cl))
                {
                    contentLength = cl;
                }
            }
        }

        if (method is null || path is null || scheme is null)
        {
            return null;
        }

        var pathOnly = path;
        var queryString = string.Empty;
        var q = path.IndexOf('?');
        if (q >= 0)
        {
            pathOnly = path[..q];
            queryString = path[q..];
        }

        if (string.IsNullOrEmpty(pathOnly))
        {
            pathOnly = "/";
        }

        var queryValues = Http1RequestReader.ParseQuery(queryString);
        var headerRo = new Dictionary<string, IReadOnlyList<string>>(headerDict.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headerDict)
        {
            headerRo[k] = v;
        }

        if (authority is not null)
        {
            headerRo["Host"] = new[] { authority };
        }

        // RFC 9220: the :protocol pseudo-header (e.g. "websocket") is surfaced to the
        // WebSocket upgrade path through a synthetic header entry.
        if (protocol is not null)
        {
            headerRo[":protocol"] = new[] { protocol };
        }

        var request = Hosting.ElsieRequestFactory.Create(
            method: method,
            path: pathOnly,
            queryString: queryString,
            queryValues: queryValues,
            headerValues: headerRo,
            body: bodyStream,
            contentLength: contentLength,
            contentType: contentType,
            requestServices: requestServices,
            requestAborted: cancellationToken,
            scheme: scheme,
            host: authority,
            protocol: "HTTP/3",
            remoteIp: ElsieRequestFactory.RemoteIpFromEndPoint(null),
            useForwardedHeaders: _serverOptions.UseForwardedHeaders);

        if (result.HasDynamicReferences)
        {
            _decoder.MarkSectionDecoded(stream.Id);
        }

        return request;
    }

    /// <summary>
    /// RFC 9220 WebSocket over HTTP/3: validates the extended CONNECT request, replies with a
    /// 2xx HEADERS frame, then runs the <see cref="ElsieWebSocket"/> handler on the stream.
    /// </summary>
    private async Task HandleWebSocketUpgradeAsync(
        QuicStream stream,
        ElsieRequest request,
        ElsieHttpResponse response,
        CancellationToken cancellationToken)
    {
        var protocol = request.GetHeader(":protocol");
        if (!string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(protocol, "websocket", StringComparison.OrdinalIgnoreCase))
        {
            var error = HostDispatch.FromResult(ElsieResult.Problem(
                400,
                "Bad Request",
                "WebSocket over HTTP/3 requires an extended CONNECT request with :protocol: websocket."));
            await WriteResponseAsync(stream, error, request.Method, cancellationToken).ConfigureAwait(false);
            return;
        }

        var respHeaders = new List<(string, string)>();
        foreach (var (name, values) in response.Headers)
        {
            foreach (var v in values)
            {
                respHeaders.Add((name.ToLowerInvariant(), v));
            }
        }

        var headerBlock = _encoder.EncodeResponse(200, respHeaders, stream.Id);
        await _encoder.FlushEncoderInstructionsAsync(cancellationToken).ConfigureAwait(false);
        await Http3FrameWriter.WriteAsync(
            stream,
            new Http3Frame(Http3FrameType.Headers, headerBlock),
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        ElsieMetrics.WebSocketConnections.Add(1);
        await using var ws = new ElsieWebSocket(new Http3WebSocketStream(stream, cancellationToken), leaveOpen: true);
        await response.WebSocketHandler!(ws, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes the response HEADERS + DATA frames, streaming BodyWriter bodies
    /// incrementally (SSE / static files) instead of buffering them in memory.</summary>
    private async Task WriteResponseAsync(
        QuicStream stream,
        ElsieHttpResponse response,
        string method,
        CancellationToken cancellationToken)
    {
        var respHeaders = new List<(string, string)>();
        if (!string.IsNullOrEmpty(response.ContentType))
        {
            respHeaders.Add(("content-type", response.ContentType!));
        }

        foreach (var (name, values) in response.Headers)
        {
            foreach (var v in values)
            {
                respHeaders.Add((name.ToLowerInvariant(), v));
            }
        }

        var isHead = string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
        var hasBufferedBody = !isHead && response.Body is { Length: > 0 };
        var hasStreamedBody = !isHead && response.BodyWriter is not null;
        var hasContentLength = respHeaders.Any(h =>
            h.Item1.Equals("content-length", StringComparison.OrdinalIgnoreCase));

        if (!hasContentLength)
        {
            if (response.Body is { } mem && !isHead)
            {
                respHeaders.Add(("content-length", mem.Length.ToString()));
            }
            else if (response.Body is { } headMem && isHead && headMem.Length > 0)
            {
                // HEAD mirrors the GET body length.
                respHeaders.Add(("content-length", headMem.Length.ToString()));
            }
            // Unknown-length BodyWriter: DATA frames delimit the body (no chunked needed on h3).
        }

        var headerBlock = _encoder.EncodeResponse(response.StatusCode, respHeaders, stream.Id);
        await _encoder.FlushEncoderInstructionsAsync(cancellationToken).ConfigureAwait(false);
        await Http3FrameWriter.WriteAsync(
            stream,
            new Http3Frame(Http3FrameType.Headers, headerBlock),
            cancellationToken).ConfigureAwait(false);

        if (hasBufferedBody)
        {
            var memory = response.Body!.Value;
            var offset = 0;
            while (offset < memory.Length)
            {
                var take = Math.Min(16 * 1024, memory.Length - offset);
                await Http3FrameWriter.WriteAsync(
                    stream,
                    new Http3Frame(Http3FrameType.Data, memory.Slice(offset, take)),
                    cancellationToken).ConfigureAwait(false);
                offset += take;
            }
        }
        else if (hasStreamedBody)
        {
            await using var dataStream = new Http3DataStream(stream, cancellationToken);
            await response.BodyWriter!(dataStream, cancellationToken).ConfigureAwait(false);
            await dataStream.FinishAsync(cancellationToken).ConfigureAwait(false);
        }

        // Trailers may have been added while a streaming writer ran (gRPC grpc-status) — re-check.
        var finalTrailers = response.Trailers;
        if (finalTrailers.Count > 0)
        {
            var trailerBlock = _encoder.EncodeTrailers(
                finalTrailers.Select(static t => (t.Key, t.Value)),
                stream.Id);
            await _encoder.FlushEncoderInstructionsAsync(cancellationToken).ConfigureAwait(false);
            await Http3FrameWriter.WriteAsync(
                stream,
                new Http3Frame(Http3FrameType.Headers, trailerBlock),
                cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task DrainDecoderInstructionsAsync(CancellationToken cancellationToken) =>
        _decoder.DrainDecoderInstructionsAsync(cancellationToken);

    private async Task CloseWithErrorAsync(long errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.CloseAsync(errorCode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is QuicException or ObjectDisposedException or OperationCanceledException)
        {
            // Connection already closing or aborted — nothing to do.
        }
    }
}
