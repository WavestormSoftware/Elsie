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
    private readonly SemaphoreSlim _writeLock = new(1, 1);

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
            if (frame.Payload.Length > _serverOptions.MaxFrameSize && frame.Type != Http2FrameType.Settings)
            {
                await GoAwayAsync(errorCode: 0x6 /* FRAME_SIZE_ERROR */, cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (frame.Type)
            {
                case Http2FrameType.Settings:
                    if ((frame.Flags & Http2FrameFlags.Ack) == 0)
                    {
                        await WriteFrameAsync(
                                Http2FrameType.Settings,
                                Http2FrameFlags.Ack,
                                0,
                                ReadOnlyMemory<byte>.Empty,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case Http2FrameType.Ping:
                    if ((frame.Flags & Http2FrameFlags.Ack) == 0 && frame.Payload.Length == 8)
                    {
                        await WriteFrameAsync(
                                Http2FrameType.Ping,
                                Http2FrameFlags.Ack,
                                0,
                                frame.Payload,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case Http2FrameType.WindowUpdate:
                    if (frame.Payload.Length >= 4 && frame.StreamId == 0)
                    {
                        var inc = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                                  (frame.Payload[2] << 8) | frame.Payload[3];
                        _serverWindow += inc;
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
                    _streams.TryRemove(frame.StreamId, out _);
                    Interlocked.Decrement(ref _activeStreams);
                    break;

                case Http2FrameType.GoAway:
                    return;

                case Http2FrameType.Priority:
                case Http2FrameType.PushPromise:
                    break;
            }
        }
    }

    private async Task OnHeadersAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
        if (frame.StreamId == 0 || (frame.StreamId & 1) == 0)
        {
            await GoAwayAsync(0x1, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_streams.ContainsKey(frame.StreamId))
        {
            await RstAsync(frame.StreamId, 0x1, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Interlocked.Increment(ref _activeStreams) > _serverOptions.MaxConcurrentStreams)
        {
            Interlocked.Decrement(ref _activeStreams);
            await RstAsync(frame.StreamId, 0x7 /* REFUSED_STREAM */, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = StripPadAndPriority(frame);
        var state = new StreamState(frame.StreamId);
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
            await RstAsync(frame.StreamId, 0x6, cancellationToken).ConfigureAwait(false);
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
            await RstAsync(frame.StreamId, 0x5 /* STREAM_CLOSED */, cancellationToken).ConfigureAwait(false);
            return;
        }

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
                await RstAsync(frame.StreamId, 0x1, cancellationToken).ConfigureAwait(false);
                return;
            }

            payload = payload[..^pad];
        }

        if (state.Body.Length + payload.Length > _serverOptions.MaxRequestBodyBytes)
        {
            await RstAsync(frame.StreamId, 0x7, cancellationToken).ConfigureAwait(false);
            _streams.TryRemove(frame.StreamId, out _);
            Interlocked.Decrement(ref _activeStreams);
            return;
        }

        state.Body.Write(payload);
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

        string method = "GET";
        string path = "/";
        string scheme = _listen.UseHttps ? "https" : "http";
        string? authority = null;
        string? contentType = null;
        long? contentLength = null;
        var headerDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in decoded)
        {
            switch (name)
            {
                case ":method": method = value; break;
                case ":path": path = value; break;
                case ":scheme": scheme = value; break;
                case ":authority": authority = value; break;
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
                    if (!name.StartsWith(":", StringComparison.Ordinal))
                    {
                        AddHeader(headerDict, name, value);
                    }

                    break;
            }
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

        byte[] body;
        if (string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            body = Array.Empty<byte>();
        }
        else if (response.Body is { } mem)
        {
            body = mem.ToArray();
        }
        else if (response.BodyWriter is not null)
        {
            await using var ms = new MemoryStream();
            await response.BodyWriter(ms, cancellationToken).ConfigureAwait(false);
            body = ms.ToArray();
        }
        else
        {
            body = Array.Empty<byte>();
        }

        if (!respHeaders.Any(h => h.Item1.Equals("content-length", StringComparison.OrdinalIgnoreCase)))
        {
            respHeaders.Add(("content-length", body.Length.ToString()));
        }

        var hpack = HpackCodec.EncodeResponse(response.StatusCode, respHeaders);
        var headerFlags = body.Length == 0
            ? Http2FrameFlags.EndStream | Http2FrameFlags.EndHeaders
            : Http2FrameFlags.EndHeaders;

        await WriteFrameAsync(Http2FrameType.Headers, headerFlags, streamId, hpack, cancellationToken)
            .ConfigureAwait(false);

        if (body.Length > 0)
        {
            // Split into max frame size chunks
            var offset = 0;
            while (offset < body.Length)
            {
                var take = Math.Min(_serverOptions.MaxFrameSize, body.Length - offset);
                var end = offset + take >= body.Length;
                await WriteFrameAsync(
                        Http2FrameType.Data,
                        end ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                        streamId,
                        body.AsMemory(offset, take),
                        cancellationToken)
                    .ConfigureAwait(false);
                offset += take;
            }
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
        // last-stream-id 0
        payload[4] = (byte)((errorCode >> 24) & 0xFF);
        payload[5] = (byte)((errorCode >> 16) & 0xFF);
        payload[6] = (byte)((errorCode >> 8) & 0xFF);
        payload[7] = (byte)(errorCode & 0xFF);
        await WriteFrameAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload, cancellationToken)
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
        public StreamState(int streamId) => StreamId = streamId;
        public int StreamId { get; }
        public MemoryStream HeaderBuffer { get; } = new();
        public MemoryStream Body { get; } = new();
        public bool HeadersComplete { get; set; }
        public bool EndStreamOnHeaders { get; set; }
        public bool EndStream { get; set; }
        public bool Dispatched { get; set; }

        public void Dispose()
        {
            HeaderBuffer.Dispose();
            Body.Dispose();
        }
    }
}
