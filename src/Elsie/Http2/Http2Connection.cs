using System.Collections.Concurrent;
using System.Net;
using Elsie.Web.Hosting;
using Elsie.Web.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web.Http2;

internal sealed class Http2Connection
{
    private readonly Stream _stream;
    private readonly IServiceProvider _services;
    private readonly ElsieListenOptions _listen;
    private readonly ElsieServerOptions _serverOptions;
    private readonly Action<string>? _log;
    private readonly EndPoint? _remote;
    private readonly HostDispatch _dispatch;
    private readonly ConcurrentDictionary<int, StreamState> _streams = new();
    private int _activeStreams;
    private int _serverWindow = 65535;
    private int _initialStreamWindow = 65535;
    private int _lastStreamId;
    private readonly HashSet<int> _seenSettingsIds = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // RFC 9113 error codes
    private const int ErrProtocol = 0x1;
    private const int ErrFlowControl = 0x3;
    private const int ErrStreamClosed = 0x5;
    private const int ErrFrameSize = 0x6;
    private const int ErrRefusedStream = 0x7;
    private const int ErrEnhanceYourCalm = 0xb;

    public Http2Connection(
        Stream stream,
        IServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        ElsieListenOptions listen,
        ElsieServerOptions serverOptions,
        Action<string>? log,
        EndPoint? remote)
    {
        _stream = stream;
        _services = services;
        _listen = listen;
        _serverOptions = serverOptions;
        _log = log;
        _remote = remote;
        _dispatch = new HostDispatch(services, dispatcher, features, serverOptions);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var preface = new byte[Http2FrameIo.ClientPreface.Length];
        if (!await ReadExactAsync(preface, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!preface.AsSpan().SequenceEqual(Http2FrameIo.ClientPreface))
        {
            _log?.Invoke("Invalid HTTP/2 client preface.");
            return;
        }

        await WriteFrameAsync(
                Http2FrameType.Settings,
                Http2FrameFlags.None,
                0,
                BuildSettings(),
                cancellationToken)
            .ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            var frameNullable = await Http2FrameIo.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (frameNullable is null)
            {
                return;
            }

            var frame = frameNullable.Value;
            if (frame.Payload.Length > _serverOptions.MaxFrameSize)
            {
                await GoAwayAsync(ErrFrameSize, cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (frame.Type)
            {
                case Http2FrameType.Settings:
                    if (!await OnSettingsAsync(frame, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case Http2FrameType.Ping:
                    if (!await OnPingAsync(frame, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case Http2FrameType.WindowUpdate:
                    if (!await OnWindowUpdateAsync(frame, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case Http2FrameType.Headers:
                    await OnHeadersAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;

                case Http2FrameType.Continuation:
                    await OnContinuationAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;

                case Http2FrameType.Data:
                    await OnDataAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;

                case Http2FrameType.RstStream:
                    if (frame.StreamId != 0 && frame.Payload.Length == 4)
                    {
                        if (_streams.TryRemove(frame.StreamId, out _))
                        {
                            Interlocked.Decrement(ref _activeStreams);
                        }
                    }
                    else if (frame.Payload.Length != 4)
                    {
                        await GoAwayAsync(ErrFrameSize, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    break;

                case Http2FrameType.GoAway:
                    return;

                case Http2FrameType.Priority:
                    // RFC 9113: PRIORITY is ignored; length must be 5 when present on stream.
                    if (frame.StreamId == 0 || frame.Payload.Length != 5)
                    {
                        await GoAwayAsync(frame.StreamId == 0 ? ErrProtocol : ErrFrameSize, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    break;

                case Http2FrameType.PushPromise:
                    // Server does not accept push from client.
                    await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    // Unknown frame types must be ignored (extensions).
                    break;
            }
        }
    }

    private async Task<bool> OnSettingsAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (frame.StreamId != 0)
        {
            await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var isAck = (frame.Flags & Http2FrameFlags.Ack) != 0;
        if (isAck)
        {
            if (frame.Payload.Length != 0)
            {
                await GoAwayAsync(ErrFrameSize, cancellationToken).ConfigureAwait(false);
                return false;
            }

            return true;
        }

        if (frame.Payload.Length % 6 != 0)
        {
            await GoAwayAsync(ErrFrameSize, cancellationToken).ConfigureAwait(false);
            return false;
        }

        _seenSettingsIds.Clear();
        for (var i = 0; i < frame.Payload.Length; i += 6)
        {
            var id = (frame.Payload[i] << 8) | frame.Payload[i + 1];
            var value = (frame.Payload[i + 2] << 24) | (frame.Payload[i + 3] << 16) |
                        (frame.Payload[i + 4] << 8) | frame.Payload[i + 5];

            if (!_seenSettingsIds.Add(id))
            {
                await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
                return false;
            }

            switch (id)
            {
                case 0x1: // HEADER_TABLE_SIZE — accept, HPACK dynamic table not fully applied yet
                    break;
                case 0x2: // ENABLE_PUSH
                    if (value is not (0 or 1))
                    {
                        await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    break;
                case 0x3: // MAX_CONCURRENT_STREAMS — client limit; ignore for server send path
                    break;
                case 0x4: // INITIAL_WINDOW_SIZE
                    if (value > 0x7FFFFFFF)
                    {
                        await GoAwayAsync(ErrFlowControl, cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    _initialStreamWindow = value;
                    break;
                case 0x5: // MAX_FRAME_SIZE
                    if (value < 16384 || value > 0xFFFFFF)
                    {
                        await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
                        return false;
                    }

                    // Peer advertised max we may send; keep our receive cap from options.
                    break;
                case 0x6: // MAX_HEADER_LIST_SIZE
                    break;
                default:
                    // Unknown settings ignored.
                    break;
            }
        }

        await WriteFrameAsync(
                Http2FrameType.Settings,
                Http2FrameFlags.Ack,
                0,
                ReadOnlyMemory<byte>.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> OnPingAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (frame.StreamId != 0)
        {
            await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (frame.Payload.Length != 8)
        {
            await GoAwayAsync(ErrFrameSize, cancellationToken).ConfigureAwait(false);
            return false;
        }

        if ((frame.Flags & Http2FrameFlags.Ack) != 0)
        {
            return true; // ignore ACK
        }

        // Echo payload exactly.
        await WriteFrameAsync(
                Http2FrameType.Ping,
                Http2FrameFlags.Ack,
                0,
                frame.Payload,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<bool> OnWindowUpdateAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (frame.Payload.Length != 4)
        {
            await GoAwayAsync(ErrFrameSize, cancellationToken).ConfigureAwait(false);
            return false;
        }

        var inc = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                  (frame.Payload[2] << 8) | frame.Payload[3];
        if (inc == 0)
        {
            if (frame.StreamId == 0)
            {
                await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RstAsync(frame.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
            }

            return frame.StreamId != 0;
        }

        if (frame.StreamId == 0)
        {
            _serverWindow += inc;
            if (_serverWindow < 0)
            {
                await GoAwayAsync(ErrFlowControl, cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        else if (_streams.TryGetValue(frame.StreamId, out var state))
        {
            state.SendWindow += inc;
        }

        return true;
    }

    private async Task OnHeadersAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (frame.StreamId == 0 || (frame.StreamId & 1) == 0)
        {
            await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (frame.StreamId <= _lastStreamId)
        {
            await GoAwayAsync(ErrProtocol, cancellationToken).ConfigureAwait(false);
            return;
        }

        _lastStreamId = frame.StreamId;

        if (_streams.ContainsKey(frame.StreamId))
        {
            await RstAsync(frame.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Interlocked.Increment(ref _activeStreams) > _serverOptions.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _activeStreams);
            await RstAsync(frame.StreamId, ErrRefusedStream, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = StripPadAndPriority(frame);
        if (payload.Length > _serverOptions.MaxHeaderBytes)
        {
            await RstAsync(frame.StreamId, ErrEnhanceYourCalm, cancellationToken).ConfigureAwait(false);
            Interlocked.Decrement(ref _activeStreams);
            return;
        }

        var state = new StreamState(frame.StreamId, _initialStreamWindow);
        state.HeaderBuffer.Write(payload);
        state.EndStreamOnHeaders = (frame.Flags & Http2FrameFlags.EndStream) != 0;
        state.HeadersComplete = (frame.Flags & Http2FrameFlags.EndHeaders) != 0;
        _streams[frame.StreamId] = state;

        if (state.HeadersComplete)
        {
            await MaybeDispatchAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnContinuationAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(frame.StreamId, out var state) || state.HeadersComplete)
        {
            await RstAsync(frame.StreamId, 0x1, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (state.HeaderBuffer.Length + frame.Payload.Length > _serverOptions.MaxHeaderBytes)
        {
            await RstAsync(frame.StreamId, ErrEnhanceYourCalm, cancellationToken).ConfigureAwait(false);
            _streams.TryRemove(frame.StreamId, out _);
            Interlocked.Decrement(ref _activeStreams);
            return;
        }

        state.HeaderBuffer.Write(frame.Payload);
        if ((frame.Flags & Http2FrameFlags.EndHeaders) != 0)
        {
            state.HeadersComplete = true;
            await MaybeDispatchAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnDataAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (!_streams.TryGetValue(frame.StreamId, out var state) || !state.HeadersComplete)
        {
            await RstAsync(frame.StreamId, ErrStreamClosed, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Connection + stream receive window (simple: decrement by full frame payload incl pad).
        var frameLen = frame.Payload.Length;
        if (frameLen > state.RecvWindow || frameLen > _serverWindow)
        {
            await GoAwayAsync(ErrFlowControl, cancellationToken).ConfigureAwait(false);
            return;
        }

        state.RecvWindow -= frameLen;
        _serverWindow -= frameLen;

        var payload = frame.Payload.AsSpan();
        if ((frame.Flags & Http2FrameFlags.Padded) != 0)
        {
            if (payload.Length == 0)
            {
                return;
            }

            var pad = payload[0];
            payload = payload[1..];
            if (pad > payload.Length)
            {
                await RstAsync(frame.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                return;
            }

            payload = payload[..^pad];
        }

        if (state.Body.Length + payload.Length > _serverOptions.MaxRequestBodyBytes)
        {
            await RstAsync(frame.StreamId, ErrRefusedStream, cancellationToken).ConfigureAwait(false);
            _streams.TryRemove(frame.StreamId, out _);
            Interlocked.Decrement(ref _activeStreams);
            return;
        }

        state.Body.Write(payload);

        // Replenish windows after consuming data.
        if (frameLen > 0)
        {
            await WriteWindowUpdateAsync(0, frameLen, cancellationToken).ConfigureAwait(false);
            await WriteWindowUpdateAsync(frame.StreamId, frameLen, cancellationToken).ConfigureAwait(false);
            state.RecvWindow += frameLen;
            _serverWindow += frameLen;
        }

        if ((frame.Flags & Http2FrameFlags.EndStream) != 0)
        {
            state.EndStream = true;
            await MaybeDispatchAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MaybeDispatchAsync(StreamState state, CancellationToken cancellationToken)
    {
        if (!state.HeadersComplete)
        {
            return;
        }

        // Need END_STREAM either on headers or after DATA (or empty body with end on headers)
        if (!state.EndStream && !state.EndStreamOnHeaders)
        {
            return; // wait for DATA
        }

        if (state.Dispatched)
        {
            return;
        }

        state.Dispatched = true;
        state.EndStream = state.EndStream || state.EndStreamOnHeaders;

        try
        {
            await DispatchStreamAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            state.Dispose();
            _streams.TryRemove(state.StreamId, out _);
            Interlocked.Decrement(ref _activeStreams);
        }
    }

    private async Task DispatchStreamAsync(StreamState state, CancellationToken cancellationToken)
    {
        List<(string Name, string Value)> decoded;
        try
        {
            decoded = HpackCodec.Decode(state.HeaderBuffer.ToArray());
        }
        catch (Exception ex)
        {
            _log?.Invoke($"HPACK error: {ex.Message}");
            await RstAsync(state.StreamId, 0x1, cancellationToken).ConfigureAwait(false);
            return;
        }

        string? method = null;
        string? path = null;
        string? scheme = null;
        string? authority = null;
        string? contentType = null;
        long? contentLength = null;
        var headerDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sawRegular = false;

        foreach (var (name, value) in decoded)
        {
            if (name.Length == 0)
            {
                await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (name[0] == ':')
            {
                if (sawRegular)
                {
                    await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                    return;
                }

                switch (name)
                {
                    case ":method":
                        if (method is not null || value.Length == 0)
                        {
                            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        method = value;
                        break;
                    case ":path":
                        if (path is not null || value.Length == 0 ||
                            (value[0] != '/' && value != "*"))
                        {
                            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        path = value;
                        break;
                    case ":scheme":
                        if (scheme is not null || value.Length == 0)
                        {
                            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        scheme = value;
                        break;
                    case ":authority":
                        if (authority is not null)
                        {
                            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        authority = value;
                        break;
                    default:
                        // Unknown/forbidden request pseudo-header.
                        await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                        return;
                }

                continue;
            }

            sawRegular = true;
            if (name is "connection" or "transfer-encoding" or "keep-alive" or "proxy-connection" or "upgrade")
            {
                await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (name)
            {
                case "content-type":
                    contentType = value;
                    AddHeader(headerDict, name, value);
                    break;
                case "content-length":
                    if (long.TryParse(value, out var cl))
                    {
                        contentLength = cl;
                    }

                    AddHeader(headerDict, name, value);
                    break;
                default:
                    AddHeader(headerDict, name, value);
                    break;
            }
        }

        if (method is null || path is null || scheme is null)
        {
            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
            return;
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

        var bodyBytes = state.Body.ToArray();
        contentLength ??= bodyBytes.Length;
        await using var bodyStream = new MemoryStream(bodyBytes, writable: false);

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

        await using var scope = _services.CreateAsyncScope();
        var request = ElsieRequestFactory.Create(
            method: method,
            path: pathOnly,
            queryString: queryString,
            queryValues: queryValues,
            headerValues: headerRo,
            body: bodyStream,
            contentLength: contentLength,
            contentType: contentType,
            requestServices: scope.ServiceProvider,
            requestAborted: cancellationToken,
            scheme: scheme,
            host: authority,
            protocol: "HTTP/2",
            remoteIp: ElsieRequestFactory.RemoteIpFromEndPoint(_remote),
            useForwardedHeaders: _serverOptions.UseForwardedHeaders);

        var response = await _dispatch.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
        await WriteResponseAsync(state.StreamId, response, method, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"H2 {method} {pathOnly} → {response.StatusCode}");
    }

    private async Task WriteResponseAsync(
        int streamId,
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
        ReadOnlyMemory<byte>? bufferedBody = null;
        if (!isHead && response.Body is { Length: > 0 } bufferedMem)
        {
            bufferedBody = bufferedMem;
        }

        var hasStreamingBody = !isHead && response.BodyWriter is not null;
        var hasTrailers = response.Trailers.Count > 0;

        if (!respHeaders.Any(h => h.Item1.Equals("content-length", StringComparison.OrdinalIgnoreCase)))
        {
            if (bufferedBody is { } body)
            {
                respHeaders.Add(("content-length", body.Length.ToString()));
            }
            else if (isHead && response.Body is { Length: > 0 } headBody)
            {
                // HEAD mirrors the GET body length.
                respHeaders.Add(("content-length", headBody.Length.ToString()));
            }
            // Unknown-length BodyWriter: DATA frames delimit the body (streamed below).
        }

        var hpack = HpackCodec.EncodeResponse(response.StatusCode, respHeaders);
        // For streaming bodies the trailer set is not known until the writer completes
        // (grpc-status is added during gRPC response writing), so HEADERS never carries
        // END_STREAM for those.
        var headerEndStream = bufferedBody is null && !hasStreamingBody && !hasTrailers;
        var headerFlags = headerEndStream
            ? Http2FrameFlags.EndStream | Http2FrameFlags.EndHeaders
            : Http2FrameFlags.EndHeaders;

        await WriteFrameAsync(Http2FrameType.Headers, headerFlags, streamId, hpack, cancellationToken)
            .ConfigureAwait(false);

        if (bufferedBody is { } buffered)
        {
            // Split into max frame size chunks
            var offset = 0;
            while (offset < buffered.Length)
            {
                var take = Math.Min(_serverOptions.MaxFrameSize, buffered.Length - offset);
                var end = offset + take >= buffered.Length;
                // With trailers pending, END_STREAM moves to the trailing HEADERS frame.
                await WriteFrameAsync(
                        Http2FrameType.Data,
                        end && !hasTrailers ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                        streamId,
                        buffered.Slice(offset, take),
                        cancellationToken)
                    .ConfigureAwait(false);
                offset += take;
            }
        }
        else if (hasStreamingBody)
        {
            await using var dataStream = new Http2ResponseBodyStream(this, streamId, _serverOptions.MaxFrameSize, cancellationToken);
            await response.BodyWriter!(dataStream, cancellationToken).ConfigureAwait(false);
            await dataStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            // Trailers may have been added while the writer ran (grpc-status) — check now.
            if (response.Trailers.Count > 0)
            {
                var trailerBlock = HpackCodec.EncodeTrailers(
                    response.Trailers.Select(static t => (t.Key, t.Value)));
                await WriteFrameAsync(
                        Http2FrameType.Headers,
                        Http2FrameFlags.EndStream | Http2FrameFlags.EndHeaders,
                        streamId,
                        trailerBlock,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteFrameAsync(
                        Http2FrameType.Data,
                        Http2FrameFlags.EndStream,
                        streamId,
                        ReadOnlyMemory<byte>.Empty,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (hasTrailers && !hasStreamingBody)
        {
            var trailerBlock = HpackCodec.EncodeTrailers(
                response.Trailers.Select(static t => (t.Key, t.Value)));
            await WriteFrameAsync(
                    Http2FrameType.Headers,
                    Http2FrameFlags.EndStream | Http2FrameFlags.EndHeaders,
                    streamId,
                    trailerBlock,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private byte[] StripPadAndPriority(Http2Frame frame)
    {
        var payload = frame.Payload.AsSpan();
        if ((frame.Flags & Http2FrameFlags.Padded) != 0)
        {
            if (payload.Length == 0)
            {
                return Array.Empty<byte>();
            }

            var pad = payload[0];
            payload = payload[1..];
            if (pad <= payload.Length)
            {
                payload = payload[..^pad];
            }
        }

        if ((frame.Flags & Http2FrameFlags.Priority) != 0)
        {
            if (payload.Length >= 5)
            {
                payload = payload[5..];
            }
        }

        return payload.ToArray();
    }

    private async Task WriteFrameAsync(
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Http2FrameIo.WriteFrameAsync(_stream, type, flags, streamId, payload, cancellationToken)
                .ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Writes a DATA frame (no END_STREAM); used by the streaming response body adapter.</summary>
    internal Task WriteDataFrameAsync(int streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        WriteFrameAsync(Http2FrameType.Data, Http2FrameFlags.None, streamId, payload, cancellationToken);

    private async Task RstAsync(int streamId, int errorCode, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        payload[0] = (byte)((errorCode >> 24) & 0xFF);
        payload[1] = (byte)((errorCode >> 16) & 0xFF);
        payload[2] = (byte)((errorCode >> 8) & 0xFF);
        payload[3] = (byte)(errorCode & 0xFF);
        await WriteFrameAsync(Http2FrameType.RstStream, Http2FrameFlags.None, streamId, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task GoAwayAsync(int errorCode, CancellationToken cancellationToken)
    {
        var payload = new byte[8];
        var last = _lastStreamId;
        payload[0] = (byte)((last >> 24) & 0x7F);
        payload[1] = (byte)((last >> 16) & 0xFF);
        payload[2] = (byte)((last >> 8) & 0xFF);
        payload[3] = (byte)(last & 0xFF);
        payload[4] = (byte)((errorCode >> 24) & 0xFF);
        payload[5] = (byte)((errorCode >> 16) & 0xFF);
        payload[6] = (byte)((errorCode >> 8) & 0xFF);
        payload[7] = (byte)(errorCode & 0xFF);
        await WriteFrameAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteWindowUpdateAsync(int streamId, int increment, CancellationToken cancellationToken)
    {
        if (increment <= 0)
        {
            return;
        }

        var payload = new byte[4];
        payload[0] = (byte)((increment >> 24) & 0x7F);
        payload[1] = (byte)((increment >> 16) & 0xFF);
        payload[2] = (byte)((increment >> 8) & 0xFF);
        payload[3] = (byte)(increment & 0xFF);
        await WriteFrameAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, streamId, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private byte[] BuildSettings()
    {
        var buf = new byte[18];
        WriteSetting(buf, 0, 0x3, _serverOptions.MaxConcurrentStreams);
        WriteSetting(buf, 6, 0x4, 65535);
        WriteSetting(buf, 12, 0x5, _serverOptions.MaxFrameSize);
        return buf;
    }

    private static void WriteSetting(byte[] buf, int offset, int id, int value)
    {
        buf[offset] = (byte)((id >> 8) & 0xFF);
        buf[offset + 1] = (byte)(id & 0xFF);
        buf[offset + 2] = (byte)((value >> 24) & 0xFF);
        buf[offset + 3] = (byte)((value >> 16) & 0xFF);
        buf[offset + 4] = (byte)((value >> 8) & 0xFF);
        buf[offset + 5] = (byte)(value & 0xFF);
    }

    private static void AddHeader(Dictionary<string, List<string>> map, string name, string value)
    {
        if (!map.TryGetValue(name, out var list))
        {
            list = new List<string>(1);
            map[name] = list;
        }

        list.Add(value);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            offset += n;
        }

        return true;
    }

    private sealed class StreamState : IDisposable
    {
        public StreamState(int streamId, int initialWindow)
        {
            StreamId = streamId;
            RecvWindow = initialWindow;
            SendWindow = initialWindow;
        }

        public int StreamId { get; }
        public MemoryStream HeaderBuffer { get; } = new();
        public MemoryStream Body { get; } = new();
        public bool HeadersComplete { get; set; }
        public bool EndStreamOnHeaders { get; set; }
        public bool EndStream { get; set; }
        public bool Dispatched { get; set; }
        public int RecvWindow { get; set; }
        public int SendWindow { get; set; }

        public void Dispose()
        {
            HeaderBuffer.Dispose();
            Body.Dispose();
        }
    }
}
