using System.Net;
using Elsie.Web.Hosting;
using Elsie.Web.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web.Http2;

internal sealed class Http2Connection
{
    private readonly Stream _stream;
    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly ElsieListenOptions _listen;
    private readonly Action<string>? _log;
    private readonly EndPoint? _remote;
    private readonly IElsieRequestFilter[] _filters;
    private readonly IElsiePrincipalAttacher[] _attachers;
    private int _serverWindow = 65535;

    public Http2Connection(
        Stream stream,
        ServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        ElsieListenOptions listen,
        Action<string>? log,
        EndPoint? remote)
    {
        _stream = stream;
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _listen = listen;
        _log = log;
        _remote = remote;
        _filters = services.GetServices<IElsieRequestFilter>().ToArray();
        _attachers = services.GetServices<IElsiePrincipalAttacher>().ToArray();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Client connection preface
        var preface = new byte[Http2FrameIo.ClientPreface.Length];
        var read = 0;
        while (read < preface.Length)
        {
            var n = await _stream.ReadAsync(preface.AsMemory(read, preface.Length - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            read += n;
        }

        if (!preface.AsSpan().SequenceEqual(Http2FrameIo.ClientPreface))
        {
            _log?.Invoke("Invalid HTTP/2 client preface.");
            return;
        }

        // Server SETTINGS
        await Http2FrameIo.WriteFrameAsync(
                _stream,
                Http2FrameType.Settings,
                Http2FrameFlags.None,
                0,
                BuildSettings(),
                cancellationToken)
            .ConfigureAwait(false);

        // Window update optional
        while (!cancellationToken.IsCancellationRequested)
        {
            var frameNullable = await Http2FrameIo.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
            if (frameNullable is null)
            {
                return;
            }

            var frame = frameNullable.Value;
            switch (frame.Type)
            {
                case Http2FrameType.Settings:
                    if ((frame.Flags & Http2FrameFlags.Ack) == 0)
                    {
                        // ACK client settings
                        await Http2FrameIo.WriteFrameAsync(
                                _stream,
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
                        await Http2FrameIo.WriteFrameAsync(
                                _stream,
                                Http2FrameType.Ping,
                                Http2FrameFlags.Ack,
                                0,
                                frame.Payload,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case Http2FrameType.WindowUpdate:
                    if (frame.Payload.Length >= 4)
                    {
                        var inc = (frame.Payload[0] << 24) | (frame.Payload[1] << 16) |
                                  (frame.Payload[2] << 8) | frame.Payload[3];
                        if (frame.StreamId == 0)
                        {
                            _serverWindow += inc & 0x7FFFFFFF;
                        }
                    }

                    break;

                case Http2FrameType.Headers:
                    await HandleHeadersAsync(frame, cancellationToken).ConfigureAwait(false);
                    break;

                case Http2FrameType.Data:
                    // Request bodies on streams — buffer then dispatch if we deferred; for v1 only GET mostly
                    break;

                case Http2FrameType.RstStream:
                case Http2FrameType.GoAway:
                    return;

                case Http2FrameType.Priority:
                case Http2FrameType.PushPromise:
                case Http2FrameType.Continuation:
                    // Ignore / unsupported for minimal subset
                    break;
            }
        }
    }

    private async Task HandleHeadersAsync(Http2Frame frame, CancellationToken cancellationToken)
    {
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
                return;
            }

            payload = payload[..^pad];
        }

        if ((frame.Flags & Http2FrameFlags.Priority) != 0)
        {
            if (payload.Length < 5)
            {
                return;
            }

            payload = payload[5..];
        }

        if ((frame.Flags & Http2FrameFlags.EndHeaders) == 0)
        {
            // CONTINUATION not implemented — reject stream
            await RstAsync(frame.StreamId, errorCode: 0x1 /* PROTOCOL_ERROR */, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        List<(string Name, string Value)> headers;
        try
        {
            headers = HpackCodec.Decode(payload);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"HPACK error: {ex.Message}");
            await RstAsync(frame.StreamId, 0x1, cancellationToken).ConfigureAwait(false);
            return;
        }

        string method = "GET";
        string path = "/";
        string scheme = _listen.UseHttps ? "https" : "http";
        string? authority = null;
        var headerDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in headers)
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
                default:
                    if (!headerDict.TryGetValue(name, out var list))
                    {
                        list = new List<string>(1);
                        headerDict[name] = list;
                    }

                    list.Add(value);
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

        // System routes + static (reuse Process path partially)
        var response = await DispatchAsync(
                method,
                pathOnly,
                queryString,
                queryValues,
                headerRo,
                scheme,
                authority,
                Stream.Null,
                contentLength: null,
                contentType: null,
                cancellationToken)
            .ConfigureAwait(false);

        // Encode response headers
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
        if (response.Body is { } mem)
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
        var endStream = body.Length == 0 ? Http2FrameFlags.EndStream | Http2FrameFlags.EndHeaders : Http2FrameFlags.EndHeaders;
        await Http2FrameIo.WriteFrameAsync(
                _stream,
                Http2FrameType.Headers,
                endStream,
                frame.StreamId,
                hpack,
                cancellationToken)
            .ConfigureAwait(false);

        if (body.Length > 0)
        {
            await Http2FrameIo.WriteFrameAsync(
                    _stream,
                    Http2FrameType.Data,
                    Http2FrameFlags.EndStream,
                    frame.StreamId,
                    body,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        _log?.Invoke($"H2 {method} {pathOnly} → {response.StatusCode}");
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ElsieHttpResponse> DispatchAsync(
        string method,
        string path,
        string queryString,
        IReadOnlyDictionary<string, IReadOnlyList<string>> queryValues,
        IReadOnlyDictionary<string, IReadOnlyList<string>> headers,
        string scheme,
        string? host,
        Stream body,
        long? contentLength,
        string? contentType,
        CancellationToken cancellationToken)
    {
        // OpenAPI / static — mirror ConnectionHandler briefly
        if (_features.OpenApi is not null &&
            (method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
             method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)))
        {
            var docPath = Normalize(_features.OpenApi.DocumentPath);
            if (string.Equals(path, docPath, StringComparison.OrdinalIgnoreCase) && _features.OpenApiJson is not null)
            {
                return FromResult(ElsieResult.Bytes(_features.OpenApiJson, "application/json; charset=utf-8"));
            }

            if (!string.IsNullOrWhiteSpace(_features.OpenApi.UiPath) &&
                string.Equals(path, Normalize(_features.OpenApi.UiPath!), StringComparison.OrdinalIgnoreCase) &&
                _features.OpenApiUiHtml is not null)
            {
                return FromResult(ElsieResult.Bytes(_features.OpenApiUiHtml, "text/html; charset=utf-8"));
            }
        }

        if (_features.StaticFiles is not null)
        {
            var staticResponse = StaticFileHandler.TryServe(method, path, _features.StaticFiles, _features.ContentRoot);
            if (staticResponse is not null)
            {
                return staticResponse;
            }
        }

        await using var scope = _services.CreateAsyncScope();
        var remoteIp = _remote switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            _ => null
        };

        var request = new ElsieRequest(
            method: method,
            path: path,
            body: body,
            contentLength: contentLength,
            contentType: contentType,
            requestServices: scope.ServiceProvider,
            requestAborted: cancellationToken,
            queryValues: queryValues,
            headerValues: headers,
            scheme: scheme,
            host: host,
            protocol: "HTTP/2",
            remoteIp: remoteIp,
            queryString: queryString);

        foreach (var a in _attachers)
        {
            a.Attach(request);
        }

        foreach (var f in _filters)
        {
            var handled = await f.TryHandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (handled is not null)
            {
                return handled;
            }
        }

        var outcome = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        return ElsieHttpResponse.FromDispatch(outcome) ?? FromResult(ElsieResult.NotFound());
    }

    private async Task RstAsync(int streamId, int errorCode, CancellationToken cancellationToken)
    {
        var payload = new byte[4];
        payload[0] = (byte)((errorCode >> 24) & 0xFF);
        payload[1] = (byte)((errorCode >> 16) & 0xFF);
        payload[2] = (byte)((errorCode >> 8) & 0xFF);
        payload[3] = (byte)(errorCode & 0xFF);
        await Http2FrameIo.WriteFrameAsync(
                _stream,
                Http2FrameType.RstStream,
                Http2FrameFlags.None,
                streamId,
                payload,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static byte[] BuildSettings()
    {
        // SETTINGS_MAX_CONCURRENT_STREAMS = 100 (id 0x3), SETTINGS_INITIAL_WINDOW_SIZE = 65535 (id 0x4)
        var buf = new byte[12];
        WriteSetting(buf, 0, 0x3, 100);
        WriteSetting(buf, 6, 0x4, 65535);
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

    private static ElsieHttpResponse FromResult(ElsieResult result) =>
        ElsieHttpResponse.FromDispatch(ElsieDispatchResult.Handled(result, new ElsieResponse()))!;

    private static string Normalize(string path) => path.StartsWith('/') ? path : "/" + path;
}
