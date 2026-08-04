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

    /// <summary>Per-connection HPACK decoder (dynamic table shared across request streams).</summary>
    private readonly HpackDecoder _hpack = new();

    private readonly HostDispatch _dispatch;
    private readonly ConcurrentDictionary<int, StreamState> _streams = new();
    private int _activeStreams;

    // Receive-side flow control (what WE advertised): decremented as DATA arrives, replenished
    // by the WINDOW_UPDATEs we write after consuming it.
    private int _connectionRecvWindow = 65535;

    // Send-side flow control (what the PEER advertised): connection-level window plus per-stream
    // StreamState.SendWindow. Guarded by _windowGate; _windowAvailable is pulsed whenever a
    // WINDOW_UPDATE (or stream removal) may have unblocked a writer. RFC 9113 §6.9.
    private readonly object _windowGate = new();
    private TaskCompletionSource _windowAvailable = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _connectionSendWindow = 65535;

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
                            lock (_windowGate)
                            {
                                // Wake a response writer blocked on send-window credit so it
                                // observes the stream is gone instead of hanging.
                                PulseWindowAvailableLocked();
                            }
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
                case 0x1: // HEADER_TABLE_SIZE
                    _hpack.SetMaxDynamicTableSize(value);
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
            lock (_windowGate)
            {
                _connectionSendWindow += inc;
                if (_connectionSendWindow > int.MaxValue)
                {
                    // RFC 9113 §6.9.1: window overflow is a connection FLOW_CONTROL_ERROR.
                    GoAwaySync(ErrFlowControl, cancellationToken);
                    return false;
                }

                PulseWindowAvailableLocked();
            }
        }
        else if (_streams.TryGetValue(frame.StreamId, out var state))
        {
            var overflow = false;
            lock (_windowGate)
            {
                state.SendWindow += inc;
                overflow = state.SendWindow > int.MaxValue;
                PulseWindowAvailableLocked();
            }

            if (overflow)
            {
                // RFC 9113 §6.9.1: stream window overflow is a stream FLOW_CONTROL_ERROR.
                await RstAsync(frame.StreamId, ErrFlowControl, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }

    /// <summary>Wakes every writer waiting on send-window credit (a WINDOW_UPDATE arrived or a
    /// stream went away). Call with <see cref="_windowGate"/> held.</summary>
    private void PulseWindowAvailableLocked()
    {
        var tcs = _windowAvailable;
        _windowAvailable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
    }

    /// <summary>Waits until the stream may send DATA (both the connection and the stream window
    /// have credit), returning the credit size. Returns 0 when the stream went away (RST /
    /// connection teardown) — the caller stops the response quietly.</summary>
    private async Task<int> WaitForSendWindowAsync(StreamState state, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_windowGate)
            {
                if (!_streams.ContainsKey(state.StreamId))
                {
                    return 0;
                }

                var available = Math.Min(_connectionSendWindow, state.SendWindow);
                if (available > 0)
                {
                    return (int)Math.Min(int.MaxValue, available);
                }

                wait = _windowAvailable.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Charges <paramref name="bytes"/> against the connection and stream send windows.</summary>
    private void ConsumeSendWindow(StreamState state, int bytes)
    {
        lock (_windowGate)
        {
            _connectionSendWindow -= bytes;
            state.SendWindow -= bytes;
        }
    }

    /// <summary>Fire-and-forget GOAWAY from inside a window-gate lock (best effort).</summary>
    private void GoAwaySync(int errorCode, CancellationToken cancellationToken) =>
        _ = GoAwayAsync(errorCode, cancellationToken);

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

        // RecvWindow is what WE advertised (our SETTINGS_INITIAL_WINDOW_SIZE, 65535);
        // SendWindow is the PEER's advertised initial window.
        var state = new StreamState(frame.StreamId, 65535, _initialStreamWindow);
        state.HeaderBuffer.Write(payload);
        state.EndStreamOnHeaders = (frame.Flags & Http2FrameFlags.EndStream) != 0;
        state.HeadersComplete = (frame.Flags & Http2FrameFlags.EndHeaders) != 0;
        _streams[frame.StreamId] = state;

        if (state.HeadersComplete)
        {
            await OnHeadersCompleteAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Shared post-HEADERS handling: decode once, then either dispatch immediately with a
    /// streaming body (gRPC — grpc clients neither half-close nor send Content-Length for unary)
    /// or wait for END_STREAM under the buffered model (with Content-Length early-dispatch).</summary>
    private async Task OnHeadersCompleteAsync(StreamState state, CancellationToken cancellationToken)
    {
        state.DecodedHeaders = TryDecodeHeaders(state.HeaderBuffer.ToArray()); state.ContentLength = ExtractContentLength(state.DecodedHeaders);
        if (IsGrpcRequest(state.DecodedHeaders))
        {
            state.IsStreaming = true;
            state.BodyStream = new Http2RequestBodyStream(_serverOptions.MaxRequestBodyBytes);
            if (state.EndStreamOnHeaders)
            {
                state.BodyStream.Complete();
            }

            state.Dispatched = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchStreamAsync(state, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    state.Dispose();
                    if (_streams.TryRemove(state.StreamId, out _))
                    {
                        Interlocked.Decrement(ref _activeStreams);
                    }
                }
            });
            return;
        }

        await MaybeDispatchAsync(state, cancellationToken).ConfigureAwait(false);
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
            await OnHeadersCompleteAsync(state, cancellationToken).ConfigureAwait(false);
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
        if (frameLen > state.RecvWindow || frameLen > _connectionRecvWindow)
        {
            await GoAwayAsync(ErrFlowControl, cancellationToken).ConfigureAwait(false);
            return;
        }

        state.RecvWindow -= frameLen;
        _connectionRecvWindow -= frameLen;

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

        if (state.IsStreaming)
        {
            // Streaming (gRPC) path: feed the handler's body stream; the connection loop stays
            // responsive so more DATA / END_STREAM can arrive. Enforce max body here (write
            // returns false on overflow → RST) and replenish flow control as DATA is consumed.
            if (state.BodyStream is not { } bodyStream)
            {
                await RstAsync(frame.StreamId, ErrRefusedStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            bodyStream.Write(payload.ToArray());
            if (bodyStream.TooLarge)
            {
                await RstAsync(frame.StreamId, ErrRefusedStream, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (frameLen > 0)
            {
                await WriteWindowUpdateAsync(0, frameLen, cancellationToken).ConfigureAwait(false);
                await WriteWindowUpdateAsync(frame.StreamId, frameLen, cancellationToken).ConfigureAwait(false);
                state.RecvWindow += frameLen;
                _connectionRecvWindow += frameLen;
            }

            if ((frame.Flags & Http2FrameFlags.EndStream) != 0)
            {
                state.EndStream = true;
                bodyStream.Complete();
            }

            return;
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
            _connectionRecvWindow += frameLen;
        }

        if ((frame.Flags & Http2FrameFlags.EndStream) != 0)
        {
            state.EndStream = true;
            await MaybeDispatchAsync(state, cancellationToken).ConfigureAwait(false);
        }
        else if (state.ContentLength is { } cl && state.Body.Length >= cl)
        {
            // Body is complete per Content-Length but the client has not sent END_STREAM
            // (grpc-go does this for unary requests). Dispatch now so message-based
            // protocols (gRPC) work; keep-alive safety: the stream state is removed after
            // dispatch, so any late frames on this stream are rejected with RST_STREAM.
            state.EndStream = true;
            await MaybeDispatchAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort decode of the request header block via the connection's HPACK
    /// decoder (dynamic-table aware). Returns null on malformed HPACK (the error surfaces
    /// properly in <see cref="DispatchStreamAsync"/>).</summary>
    private List<(string Name, string Value)>? TryDecodeHeaders(byte[] headerBlock)
    {
        try
        {
            return _hpack.Decode(headerBlock);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Extracts <c>content-length</c> from decoded request headers; null when absent.
    /// Used to dispatch a request whose body is fully received per Content-Length even when
    /// the client omits END_STREAM.</summary>
    private static long? ExtractContentLength(List<(string Name, string Value)>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var (name, value) in headers)
        {
            if (name.Equals("content-length", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(value, out var cl))
            {
                return cl;
            }
        }

        return null;
    }

    /// <summary>True when the request is a gRPC call (content-type <c>application/grpc</c> or a
    /// gRPC variant), which must be dispatched on HEADERS with a streaming body because gRPC
    /// clients (grpc-go) do not half-close the request stream or send Content-Length.</summary>
    private static bool IsGrpcRequest(List<(string Name, string Value)>? headers)
    {
        if (headers is null)
        {
            return false;
        }

        foreach (var (name, value) in headers)
        {
            if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase) &&
                value.StartsWith("application/grpc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Starts dispatch on a background task once the request is complete. The
    /// connection read loop never awaits a dispatch — concurrent streams are served in
    /// parallel (HTTP/2 multiplexing, RFC 9113 §5.1).</summary>
    private Task MaybeDispatchAsync(StreamState state, CancellationToken cancellationToken)
    {
        if (!state.HeadersComplete)
        {
            return Task.CompletedTask;
        }

        // Need END_STREAM either on headers or after DATA (or empty body with end on headers)
        if (!state.EndStream && !state.EndStreamOnHeaders)
        {
            return Task.CompletedTask; // wait for DATA
        }

        if (state.Dispatched)
        {
            return Task.CompletedTask;
        }

        state.Dispatched = true;
        state.EndStream = state.EndStream || state.EndStreamOnHeaders;

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchStreamAsync(state, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                state.Dispose();
                if (_streams.TryRemove(state.StreamId, out _))
                {
                    Interlocked.Decrement(ref _activeStreams);
                }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task DispatchStreamAsync(StreamState state, CancellationToken cancellationToken)
    {
        // Headers were decoded on the connection loop (HPACK decode is strictly ordered and
        // must not run concurrently on dispatch threads). A null here means the block was
        // malformed — the decode error already happened on the loop.
        if (state.DecodedHeaders is not { } decoded)
        {
            _log?.Invoke("HPACK error: malformed header block.");
            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
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

        var isConnect = string.Equals(method, "CONNECT", StringComparison.OrdinalIgnoreCase);
        var isOptionsStar = string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "*", StringComparison.Ordinal);

        // RFC 9113 §8.3.1: :method, :scheme, and :path are required for all requests EXCEPT
        // CONNECT (which has no :scheme or :path — its target is the :authority / authority form).
        if (method is null || (!isConnect && (path is null || scheme is null)))
        {
            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
            return;
        }

        // RFC 9113 §8.3.1 / RFC 9110 §7.1: the :authority pseudo-header is required for all
        // requests except CONNECT and OPTIONS * (asterisk-form target has no authority).
        if (authority is null && !isConnect && !isOptionsStar)
        {
            await RstAsync(state.StreamId, ErrProtocol, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pathOnly = path ?? "/";
        var queryString = string.Empty;
        var q = pathOnly.IndexOf('?');
        if (q >= 0)
        {
            queryString = pathOnly[q..];
            pathOnly = pathOnly[..q];
        }

        if (string.IsNullOrEmpty(pathOnly))
        {
            pathOnly = "/";
        }

        var bodyBytes = state.BodyStream is not null ? Array.Empty<byte>() : state.Body.ToArray();
        contentLength ??= state.BodyStream is not null ? null : bodyBytes.Length;
        var bodyStream = state.BodyStream is not null
            ? (Stream)state.BodyStream
            : new MemoryStream(bodyBytes, writable: false);

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
        await WriteResponseAsync(state, response, method, cancellationToken).ConfigureAwait(false);
        _log?.Invoke($"H2 {method} {pathOnly} → {response.StatusCode}");
    }

    private async Task WriteResponseAsync(
        StreamState state,
        ElsieHttpResponse response,
        string method,
        CancellationToken cancellationToken)
    {
        var streamId = state.StreamId;
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

        // RFC 9118: send 103 Early Hints HEADERS frames (with Link headers) before the final response.
        if (response.EarlyHints.Count > 0 && response.WebSocketHandler is null)
        {
            foreach (var link in response.EarlyHints)
            {
                var hintBlock = HpackCodec.EncodeResponse(103, [("link", link)]);
                await WriteFrameAsync(
                        Http2FrameType.Headers,
                        Http2FrameFlags.EndHeaders,
                        streamId,
                        hintBlock,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
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
            // Split into chunks bounded by the max frame size AND the peer's flow-control
            // windows (RFC 9113 §6.9): never send more than the connection/stream credit.
            var offset = 0;
            while (offset < buffered.Length)
            {
                var window = await WaitForSendWindowAsync(state, cancellationToken).ConfigureAwait(false);
                if (window == 0)
                {
                    return; // stream reset / connection teardown while sending
                }

                var take = Math.Min(Math.Min(_serverOptions.MaxFrameSize, window), buffered.Length - offset);
                ConsumeSendWindow(state, take);
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

    /// <summary>Writes a DATA frame (no END_STREAM) within the peer's flow-control windows
    /// (RFC 9113 §6.9); used by the streaming response body adapter. Splits the payload when
    /// the available credit is smaller than the frame and drops the remainder when the stream
    /// went away mid-write.</summary>
    internal async Task WriteDataFrameAsync(int streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!_streams.TryGetValue(streamId, out var state))
            {
                return; // stream gone — nothing left to send to
            }

            var window = await WaitForSendWindowAsync(state, cancellationToken).ConfigureAwait(false);
            if (window == 0)
            {
                return;
            }

            var take = Math.Min(window, payload.Length - offset);
            ConsumeSendWindow(state, take);
            await WriteFrameAsync(
                    Http2FrameType.Data,
                    Http2FrameFlags.None,
                    streamId,
                    payload.Slice(offset, take),
                    cancellationToken)
                .ConfigureAwait(false);
            offset += take;
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
        public StreamState(int streamId, int recvWindow, long sendWindow)
        {
            StreamId = streamId;
            RecvWindow = recvWindow;
            SendWindow = sendWindow;
        }

        public int StreamId { get; }
        public MemoryStream HeaderBuffer { get; } = new();
        public MemoryStream Body { get; } = new();
        public bool HeadersComplete { get; set; }
        public bool EndStreamOnHeaders { get; set; }
        public bool EndStream { get; set; }
        public bool Dispatched { get; set; }
        public int RecvWindow { get; set; }
        public long SendWindow { get; set; }

        /// <summary>Optional <c>Content-Length</c> from the request headers; lets the server dispatch
        /// a request once its body is fully received even if the client never sends END_STREAM
        /// (grpc-go sends unary requests this way).</summary>
        public long? ContentLength { get; set; }

        /// <summary>Decoded request headers (from the HPACK block) — reused by dispatch.</summary>
        public List<(string Name, string Value)>? DecodedHeaders { get; set; }

        /// <summary>True when the request content-type is gRPC — dispatched on HEADERS with a
        /// streaming body (grpc clients do not send END_STREAM / Content-Length).</summary>
        public bool IsStreaming { get; set; }

        /// <summary>Streaming request body fed by DATA frames (gRPC path).</summary>
        public Http2RequestBodyStream? BodyStream { get; set; }

        public void Dispose()
        {
            HeaderBuffer.Dispose();
            Body.Dispose();
        }
    }
}
