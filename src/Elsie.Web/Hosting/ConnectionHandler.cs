using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Elsie.Web.Http;
using Elsie.Web.Http2;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web.Hosting;

internal sealed class ConnectionHandler
{
    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly ElsieListenOptions _listen;
    private readonly ElsieServerOptions _serverOptions;
    private readonly Action<string>? _log;
    private readonly IElsieRequestFilter[] _filters;
    private readonly IElsiePrincipalAttacher[] _principalAttachers;

    public ConnectionHandler(
        ServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        ElsieListenOptions listen,
        ElsieServerOptions serverOptions,
        Action<string>? log)
    {
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _listen = listen;
        _serverOptions = serverOptions ?? new ElsieServerOptions();
        _log = log;
        _filters = services.GetServices<IElsieRequestFilter>().ToArray();
        _principalAttachers = services.GetServices<IElsiePrincipalAttacher>().ToArray();
    }

    public async Task RunAsync(Socket socket, CancellationToken cancellationToken)
    {
        await using var network = new NetworkStream(socket, ownsSocket: true);
        Stream stream = network;

        try
        {
            if (_listen.UseHttps)
            {
                if (_listen.Certificate is null)
                {
                    throw new InvalidOperationException("HTTPS listen requires a certificate.");
                }

                var ssl = new SslStream(network, leaveInnerStreamOpen: false);
                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _listen.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = BuildAlpn()
                };
                await ssl.AuthenticateAsServerAsync(sslOptions, cancellationToken).ConfigureAwait(false);
                stream = ssl;

                if (ssl.NegotiatedApplicationProtocol.Equals(SslApplicationProtocol.Http2))
                {
                    var h2 = new Http2Connection(
                        stream,
                        _services,
                        _dispatcher,
                        _features,
                        _listen,
                        _serverOptions,
                        _log,
                        socket.RemoteEndPoint);
                    await h2.RunAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            await RunHttp1Async(stream, socket.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // shutting down
        }
        catch (IOException)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Connection error: {ex.Message}");
        }
        finally
        {
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private List<SslApplicationProtocol>? BuildAlpn()
    {
        var list = new List<SslApplicationProtocol>();
        if (_listen.Protocols.HasFlag(ElsieHttpProtocols.Http2))
        {
            list.Add(SslApplicationProtocol.Http2);
        }

        if (_listen.Protocols.HasFlag(ElsieHttpProtocols.Http1))
        {
            list.Add(SslApplicationProtocol.Http11);
        }

        return list.Count == 0 ? null : list;
    }

    private async Task RunHttp1Async(Stream stream, EndPoint? remote, CancellationToken cancellationToken)
    {
        var reader = new Http1RequestReader(
            stream,
            _serverOptions.MaxRequestLineLength,
            _serverOptions.MaxHeaderBytes,
            _serverOptions.MaxRequestBodyBytes);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ParsedHttpRequest? parsed;
                try
                {
                    parsed = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    await WriteSimpleErrorAsync(stream, 400, ex.Message, keepAlive: false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (parsed is null)
                {
                    return;
                }

                var start = Stopwatch.GetTimestamp();
                var response = await ProcessAsync(parsed, remote, cancellationToken).ConfigureAwait(false);

                if (response.WebSocketHandler is not null)
                {
                    if (!WebSocketUpgrade.IsUpgradeRequest(parsed))
                    {
                        await WriteSimpleErrorAsync(
                                stream,
                                400,
                                "WebSocket upgrade headers required.",
                                keepAlive: false,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var key = parsed.Headers.TryGetValue("Sec-WebSocket-Key", out var keys) && keys.Count > 0
                        ? keys[0]
                        : null;
                    if (string.IsNullOrEmpty(key))
                    {
                        await WriteSimpleErrorAsync(stream, 400, "Missing Sec-WebSocket-Key.", false, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    await WebSocketUpgrade.WriteHandshakeAsync(stream, parsed.Protocol, key!, cancellationToken)
                        .ConfigureAwait(false);
                    var msWs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    _log?.Invoke($"{parsed.Method} {parsed.Path} → 101 {msWs:0}ms");
                    await parsed.Body.DisposeAsync().ConfigureAwait(false);

                    await using var ws = new ElsieWebSocket(stream, leaveOpen: true);
                    try
                    {
                        await response.WebSocketHandler(ws, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"WebSocket error: {ex.Message}");
                    }

                    return; // connection consumed by WebSocket
                }

                var keepAlive = parsed.KeepAlive;
                var isHead = HttpMethods.IsHead(parsed.Method);
                var isStreaming = response.BodyWriter is not null &&
                                  string.Equals(response.ContentType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

                if (isStreaming && !isHead)
                {
                    await Http1ResponseWriter.WriteChunkedAsync(
                            stream,
                            response,
                            parsed.Protocol,
                            keepAlive: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    keepAlive = false;
                }
                else
                {
                    await Http1ResponseWriter.WriteAsync(
                            stream,
                            response,
                            parsed.Protocol,
                            keepAlive,
                            isHead,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _log?.Invoke($"{parsed.Method} {parsed.Path} → {response.StatusCode} {ms:0}ms");

                await parsed.Body.DisposeAsync().ConfigureAwait(false);

                if (!keepAlive)
                {
                    return;
                }
            }
        }
        finally
        {
            reader.DisposeBuffer();
        }
    }

    private async Task<ElsieHttpResponse> ProcessAsync(
        ParsedHttpRequest parsed,
        EndPoint? remote,
        CancellationToken cancellationToken)
    {
        // OpenAPI system routes
        if (_features.OpenApi is not null && HttpMethods.IsGetOrHead(parsed.Method))
        {
            var docPath = NormalizePath(_features.OpenApi.DocumentPath);
            if (string.Equals(parsed.Path, docPath, StringComparison.OrdinalIgnoreCase) &&
                _features.OpenApiJson is not null)
            {
                return FromResult(ElsieResult.Bytes(_features.OpenApiJson, "application/json; charset=utf-8"));
            }

            if (!string.IsNullOrWhiteSpace(_features.OpenApi.UiPath) &&
                string.Equals(parsed.Path, NormalizePath(_features.OpenApi.UiPath!), StringComparison.OrdinalIgnoreCase) &&
                _features.OpenApiUiHtml is not null)
            {
                return FromResult(ElsieResult.Bytes(_features.OpenApiUiHtml, "text/html; charset=utf-8"));
            }
        }

        // Static files
        if (_features.StaticFiles is not null)
        {
            var staticResponse = StaticFileHandler.TryServe(
                parsed.Method,
                parsed.Path,
                _features.StaticFiles,
                _features.ContentRoot);
            if (staticResponse is not null)
            {
                return staticResponse;
            }
        }

        await using var scope = _services.CreateAsyncScope();
        var headerRo = new Dictionary<string, IReadOnlyList<string>>(parsed.Headers.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in parsed.Headers)
        {
            headerRo[k] = v;
        }

        var remoteIp = remote switch
        {
            IPEndPoint ip => ip.Address.ToString(),
            _ => null
        };

        var host = FirstHeader(parsed.Headers, "Host");
        var scheme = _listen.UseHttps ? "https" : "http";

        var request = new ElsieRequest(
            method: parsed.Method,
            path: parsed.Path,
            body: parsed.Body,
            contentLength: parsed.ContentLength,
            contentType: parsed.ContentType,
            requestServices: scope.ServiceProvider,
            requestAborted: cancellationToken,
            queryValues: parsed.QueryValues,
            headerValues: headerRo,
            scheme: scheme,
            host: host,
            pathBase: null,
            protocol: parsed.Protocol,
            remoteIp: remoteIp,
            queryString: parsed.QueryString);

        foreach (var attacher in _principalAttachers)
        {
            attacher.Attach(request);
        }

        foreach (var filter in _filters)
        {
            var handled = await filter.TryHandleAsync(request, cancellationToken).ConfigureAwait(false);
            if (handled is not null)
            {
                return handled;
            }
        }

        var outcome = await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        var response = ElsieHttpResponse.FromDispatch(outcome);
        if (response is null)
        {
            return FromResult(ElsieResult.NotFound());
        }

        return response;
    }

    private static ElsieHttpResponse FromResult(ElsieResult result) =>
        ElsieHttpResponse.FromDispatch(ElsieDispatchResult.Handled(result, new ElsieResponse()))!;

    private static string? FirstHeader(Dictionary<string, List<string>> headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    private static string NormalizePath(string path) =>
        path.StartsWith('/') ? path : "/" + path;

    private static async Task WriteSimpleErrorAsync(
        Stream stream,
        int status,
        string message,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        var response = FromResult(ElsieResult.Problem(status, HttpReasonPhrases.Get(status), message));
        await Http1ResponseWriter.WriteAsync(stream, response, "HTTP/1.1", keepAlive, headRequest: false, cancellationToken)
            .ConfigureAwait(false);
    }
}
