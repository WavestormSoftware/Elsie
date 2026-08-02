using System.Net.Quic;
using Elsie;
using Elsie.Web.Hosting;
using Elsie.Web.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsie.Web.Http3;

/// <summary>
/// HTTP/3 connection handler (RFC 9114): opens the server control stream, reads the
/// client's unidirectional streams (control + QPACK), and serves bidirectional request
/// streams through the same <c>HostDispatch</c> as HTTP/1.1 / HTTP/2.
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
        // Server control stream (unidirectional): type + SETTINGS.
        try
        {
            await using var controlStream = await connection.OpenOutboundStreamAsync(
                QuicStreamType.Unidirectional,
                cancellationToken).ConfigureAwait(false);
            await Http3ControlStreams.WriteServerPreambleAsync(controlStream, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException)
        {
            return;
        }

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
                _ = Task.Run(() => Http3ControlStreams.ReadClientUnidirectionalStreamAsync(s, cancellationToken));
                continue;
            }

            // Bidirectional request stream.
            _ = Task.Run(() => HandleRequestStreamAsync(s, cancellationToken));
        }
    }

    private async Task HandleRequestStreamAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        await using var s = stream;
        try
        {
            var request = await ReadRequestAsync(s, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return;
            }

            var response = await _dispatch.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
            if (BodyStream!.IsTooLarge)
            {
                response = HostDispatch.FromResult(ElsieResult.Problem(
                    413,
                    "Request Entity Too Large",
                    "Request body exceeds the configured maximum."));
            }

            await WriteResponseAsync(s, response, request.Method, cancellationToken).ConfigureAwait(false);
            _log?.Invoke($"H3 {request.Method} {request.Path} → {response.StatusCode}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or QuicException)
        {
            // Client aborted the stream — nothing to write.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HTTP/3 request stream failed.");
        }
    }

    /// <summary>Reads the HEADERS frame, decodes QPACK, and builds an <see cref="ElsieRequest"/>.</summary>
    private async Task<ElsieRequest?> ReadRequestAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        var bodyStream = new QuicRequestBodyStream(stream, _serverOptions.MaxRequestBodyBytes);
        var request = await ReadRequestCoreAsync(stream, bodyStream, cancellationToken).ConfigureAwait(false);
        if (request is not null)
        {
            bodyStream.StartReadingAsync(cancellationToken);
            BodyStream = bodyStream;
        }

        return request;
    }

    /// <summary>Body stream for the in-flight request (used for 413 detection).</summary>
    private QuicRequestBodyStream? BodyStream { get; set; }

    private async Task<ElsieRequest?> ReadRequestCoreAsync(
        QuicStream stream,
        QuicRequestBodyStream bodyStream,
        CancellationToken cancellationToken)
    {
        // Request HEADERS frame (DATA body follows; buffered for the minimal server).
        var first = await Http3FrameReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (first is null)
        {
            return null;
        }

        if (first.Value.Type != Http3FrameType.Headers)
        {
            return null;
        }

        var fields = new QpackDecoder().DecodeHeaderBlock(first.Value.Payload.Span);

        string? method = null, path = null, scheme = null, authority = null;
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

        // Request body arrives as DATA frames after the HEADERS frame and is served lazily
        // (pump started by the caller once the request is built).

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
        return Hosting.ElsieRequestFactory.Create(
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
            protocol: "HTTP/3",
            remoteIp: ElsieRequestFactory.RemoteIpFromEndPoint(null),
            useForwardedHeaders: _serverOptions.UseForwardedHeaders);
    }

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

        var hasTrailers = response.Trailers.Count > 0;
        var headerBlock = QpackEncoder.EncodeResponse(response.StatusCode, respHeaders);
        await Http3FrameWriter.WriteAsync(
            stream,
            new Http3Frame(Http3FrameType.Headers, headerBlock),
            cancellationToken).ConfigureAwait(false);

        if (body.Length > 0)
        {
            await Http3FrameWriter.WriteAsync(
                stream,
                new Http3Frame(Http3FrameType.Data, body),
                cancellationToken).ConfigureAwait(false);
        }

        if (hasTrailers)
        {
            var trailerBlock = QpackEncoder.EncodeTrailers(
                response.Trailers.Select(static t => (t.Key, t.Value)));
            await Http3FrameWriter.WriteAsync(
                stream,
                new Http3Frame(Http3FrameType.Headers, trailerBlock),
                cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
