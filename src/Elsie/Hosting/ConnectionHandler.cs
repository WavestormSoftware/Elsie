using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Elsie.Web.Http;
using Elsie.Web.Http2;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsie.Web.Hosting;

internal sealed class ConnectionHandler
{
    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly ElsieListenOptions _listen;
    private readonly ElsieServerOptions _serverOptions;
    private readonly Action<string>? _log;
    private readonly ILogger _logger;
    private readonly HostDispatch _dispatch;

    public ConnectionHandler(
        ServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        ElsieListenOptions listen,
        ElsieServerOptions serverOptions,
        Action<string>? log,
        ILogger? logger = null)
    {
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _listen = listen;
        _serverOptions = serverOptions ?? new ElsieServerOptions();
        _log = log;
        _logger = logger ?? NullLogger.Instance;
        _dispatch = new HostDispatch(services, dispatcher, features, _serverOptions);
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
        }
        catch (IOException)
        {
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
                    using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (_serverOptions.RequestHeadersTimeout > TimeSpan.Zero)
                    {
                        headerCts.CancelAfter(_serverOptions.RequestHeadersTimeout);
                    }

                    parsed = await reader.ReadAsync(headerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await WriteErrorAsync(stream, 408, "Request Timeout", "Header read timed out.", keepAlive: false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (InvalidOperationException ex)
                {
                    var status = ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase) ? 413 : 400;
                    var title = status == 413 ? "Payload Too Large" : "Bad Request";
                    await WriteErrorAsync(stream, status, title, ex.Message, keepAlive: false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (parsed is null)
                {
                    return;
                }

                var start = Stopwatch.GetTimestamp();
                ElsieHttpResponse response;
                await using (var scope = _services.CreateAsyncScope())
                {
                    var headerRo = new Dictionary<string, IReadOnlyList<string>>(
                        parsed.Headers.Count,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in parsed.Headers)
                    {
                        headerRo[k] = v;
                    }

                    var request = ElsieRequestFactory.Create(
                        method: parsed.Method,
                        path: parsed.Path,
                        queryString: parsed.QueryString,
                        queryValues: parsed.QueryValues,
                        headerValues: headerRo,
                        body: parsed.Body,
                        contentLength: parsed.ContentLength,
                        contentType: parsed.ContentType,
                        requestServices: scope.ServiceProvider,
                        requestAborted: cancellationToken,
                        scheme: _listen.UseHttps ? "https" : "http",
                        host: FirstHeader(parsed.Headers, "Host"),
                        protocol: parsed.Protocol,
                        remoteIp: ElsieRequestFactory.RemoteIpFromEndPoint(remote),
                        useForwardedHeaders: _serverOptions.UseForwardedHeaders);

                    response = await _dispatch.ProcessAsync(request, cancellationToken).ConfigureAwait(false);
                }

                if (response.WebSocketHandler is not null)
                {
                    if (!WebSocketUpgrade.IsUpgradeRequest(parsed))
                    {
                        await WriteErrorAsync(
                                stream,
                                400,
                                "Bad Request",
                                "WebSocket upgrade headers required.",
                                keepAlive: false,
                                cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    var key = FirstHeader(parsed.Headers, "Sec-WebSocket-Key");
                    if (string.IsNullOrEmpty(key))
                    {
                        await WriteErrorAsync(stream, 400, "Bad Request", "Missing Sec-WebSocket-Key.", false, cancellationToken)
                            .ConfigureAwait(false);
                        return;
                    }

                    await WebSocketUpgrade.WriteHandshakeAsync(stream, parsed.Protocol, key!, cancellationToken)
                        .ConfigureAwait(false);
                    _log?.Invoke($"{parsed.Method} {parsed.Path} → 101 {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0}ms");
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

                    return;
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

                _log?.Invoke(
                    $"{parsed.Method} {parsed.Path} → {response.StatusCode} {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0}ms");

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

    private static string? FirstHeader(Dictionary<string, List<string>> headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    private static async Task WriteErrorAsync(
        Stream stream,
        int status,
        string title,
        string message,
        bool keepAlive,
        CancellationToken cancellationToken)
    {
        var response = HostDispatch.FromResult(ElsieResult.Problem(status, title, message));
        await Http1ResponseWriter.WriteAsync(stream, response, "HTTP/1.1", keepAlive, headRequest: false, cancellationToken)
            .ConfigureAwait(false);
    }
}
