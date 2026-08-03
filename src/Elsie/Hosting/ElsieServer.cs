using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using Elsie.Web.Http3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsie.Web.Hosting;

internal sealed class ElsieServer : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly bool _ownsServices;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly IReadOnlyList<ElsieListenOptions> _endpoints;
    private readonly ElsieServerOptions _serverOptions;
    private readonly Action<string>? _log;
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<TcpListener> _listeners = new();
    private readonly List<Socket> _unixListeners = new();
    private readonly List<string> _unixPaths = new();
#pragma warning disable CA1416 // guarded by QuicListener.IsSupported + OS checks at runtime
    private readonly List<QuicListener> _quicListeners = new();
#pragma warning restore CA1416
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _acceptLoops = new();
    private readonly ConcurrentDictionary<Guid, ConnectionEntry> _connections = new();
    private readonly SemaphoreSlim _connectionSlots;
    private int _started;

    public ElsieServer(
        IServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        IReadOnlyList<ElsieListenOptions> endpoints,
        ElsieServerOptions serverOptions,
        Action<string>? log,
        ILoggerFactory? loggerFactory = null,
        bool ownsServices = true)
    {
        _services = services;
        _ownsServices = ownsServices;
        _dispatcher = dispatcher;
        _features = features;
        _endpoints = endpoints;
        _serverOptions = serverOptions ?? new ElsieServerOptions();
        _log = log;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger("Elsie.Server");
        var max = Math.Max(1, _serverOptions.MaxConcurrentConnections);
        _connectionSlots = new SemaphoreSlim(max, max);
    }

    public IReadOnlyList<IPEndPoint> BoundEndpoints { get; private set; } = Array.Empty<IPEndPoint>();

    /// <summary>Unix domain socket paths this server is listening on.</summary>
    public IReadOnlyList<string> BoundUnixSocketPaths { get; private set; } = Array.Empty<string>();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Server already started.");
        }

        var routes = _services.GetRequiredService<Routing.RouteTable>();
        _features.WarmOpenApi(routes);

        var bound = new List<IPEndPoint>();
        foreach (var ep in _endpoints)
        {
            if (ep.IsUnixSocket)
            {
                StartUnixListener(ep);
                continue;
            }

            var listener = new TcpListener(ep.Address, ep.Port);
            listener.Start(Math.Max(1, _serverOptions.ListenBacklog));
            var local = (IPEndPoint)listener.LocalEndpoint;
            bound.Add(local);
            _listeners.Add(listener);
            var scheme = ep.UseHttps ? "https" : "http";
            var msg = $"Listening on {scheme}://{FormatHost(local)}/";
            _log?.Invoke(msg);
            _logger.LogInformation("{Message}", msg);
            _acceptLoops.Add(AcceptLoopAsync(listener, ep, _cts.Token));
            StartHttp3ListenerIfSupported(ep, local.Port);
        }

        BoundEndpoints = bound;
        BoundUnixSocketPaths = _unixPaths.ToArray();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the HTTP/3 (QUIC) listener for <paramref name="ep"/> when enabled and the
    /// platform has QUIC support (libmsquic). Silently skipped otherwise.
    /// </summary>
    /// <param name="ep">The listen options.</param>
    /// <param name="tcpPort">The TCP listener's actual bound port, so HTTP/3 (UDP) shares the
    /// same numeric port — required for ephemeral (port 0) listens and for clients that resolve
    /// one endpoint for both transports.</param>
#pragma warning disable CA1416 // guarded by QuicListener.IsSupported + OS checks at runtime
    private void StartHttp3ListenerIfSupported(ElsieListenOptions ep, int tcpPort)
    {
        if (!ep.EnableHttp3 || !ep.UseHttps || ep.Certificate is null || !QuicListener.IsSupported)
        {
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var options = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(ep.Address, tcpPort),
            ApplicationProtocols = [SslApplicationProtocol.Http3],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = ep.Certificate,
                    ApplicationProtocols = [SslApplicationProtocol.Http3]
                },
                MaxInboundBidirectionalStreams = _serverOptions.MaxConcurrentStreams > 0 ? _serverOptions.MaxConcurrentStreams : 100,
                MaxInboundUnidirectionalStreams = 10,
                DefaultStreamErrorCode = 0x0100, // H3_NO_ERROR
                DefaultCloseErrorCode = 0x0100  // H3_NO_ERROR
            })
        };

        QuicListener listener;
        try
        {
            listener = QuicListener.ListenAsync(options, _cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start HTTP/3 listener on udp://{Host}:{Port} — continuing without HTTP/3.", ep.Address, tcpPort);
            return;
        }

        _quicListeners.Add(listener);
        var local = listener.LocalEndPoint;
        var msg = $"Listening on https+quic://{FormatHost(local)}/";
        _log?.Invoke(msg);
        _logger.LogInformation("{Message}", msg);
        _acceptLoops.Add(AcceptHttp3LoopAsync(listener, ep, _cts.Token));
    }

    private async Task AcceptHttp3LoopAsync(QuicListener listener, ElsieListenOptions ep, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await listener.AcceptConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (QuicException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            var conn = connection;
            _ = Task.Run(
                () => RunHttp3ConnectionAsync(conn, ep, ct),
                CancellationToken.None);
        }
    }

    private async Task RunHttp3ConnectionAsync(QuicConnection connection, ElsieListenOptions ep, CancellationToken ct)
    {
        await using var conn = connection;
        var dispatch = new HostDispatch(
            _services,
            _dispatcher,
            _features,
            _serverOptions,
            _loggerFactory);
        var handler = new Http3Connection(_services, dispatch, _serverOptions, _log, _loggerFactory);
        await handler.RunAsync(conn, ct).ConfigureAwait(false);
    }
#pragma warning restore CA1416

    private void StartUnixListener(ElsieListenOptions ep)
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            throw new PlatformNotSupportedException("Unix domain sockets are not supported on this platform.");
        }

        if (ep.UseHttps)
        {
            throw new InvalidOperationException("HTTPS is not supported on Unix domain sockets; terminate TLS on a reverse proxy.");
        }

        var path = Path.GetFullPath(ep.UnixSocketPath!);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(Math.Max(1, _serverOptions.ListenBacklog));
        _unixListeners.Add(socket);
        _unixPaths.Add(path);

        // Force HTTP/1.1 only — no ALPN on UDS.
        ep.Protocols = ElsieHttpProtocols.Http1;
        ep.UseHttps = false;

        var msg = $"Listening on http+unix://{path}";
        _log?.Invoke(msg);
        _logger.LogInformation("{Message}", msg);
        _acceptLoops.Add(AcceptUnixLoopAsync(socket, ep, _cts.Token));
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        foreach (var listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // ignore
            }
        }

        foreach (var listener in _unixListeners)
        {
            try
            {
                listener.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        foreach (var listener in _quicListeners)
        {
            try
            {
#pragma warning disable CA1416 // guarded at runtime by QuicListener.IsSupported + OS checks
                listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore CA1416
            }
            catch
            {
                // ignore
            }
        }

        foreach (var path in _unixPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            await Task.WhenAll(_acceptLoops).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        // Drain in-flight connections
        var pending = _connections.Values.Select(c => c.Task).ToArray();
        if (pending.Length > 0)
        {
            var drain = Task.WhenAll(pending);
            var completed = await Task.WhenAny(drain, Task.Delay(_serverOptions.ConnectionDrainTimeout))
                .ConfigureAwait(false);
            if (completed != drain)
            {
                var remaining = _connections.Values.ToArray();
                _logger.LogWarning("Connection drain timed out with {Count} still active.", remaining.Length);

                if (_serverOptions.ShutdownAbortConnections)
                {
                    foreach (var entry in remaining)
                    {
                        try { entry.ConnectionCts.Cancel(); } catch { /* ignore */ }
                        try { entry.Socket.Dispose(); } catch { /* ignore */ }
                    }

                    // Brief wait so aborted handlers unwind.
                    try
                    {
                        await Task.WhenAny(Task.WhenAll(remaining.Select(e => e.Task)), Task.Delay(TimeSpan.FromSeconds(1)))
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
    }

    // IHostedService.StopAsync ignores the token — drain uses ConnectionDrainTimeout.
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync();

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _connectionSlots.Dispose();
        if (_ownsServices && _services is IAsyncDisposable asyncDisp)
        {
            await asyncDisp.DisposeAsync().ConfigureAwait(false);
        }
        else if (_ownsServices && _services is IDisposable disp)
        {
            disp.Dispose();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, ElsieListenOptions options, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            StartConnection(socket, options, applyTcpOptions: true, ct);
        }
    }

    private async Task AcceptUnixLoopAsync(Socket listener, ElsieListenOptions options, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            StartConnection(socket, options, applyTcpOptions: false, ct);
        }
    }

    private void StartConnection(Socket socket, ElsieListenOptions options, bool applyTcpOptions, CancellationToken ct)
    {
        if (!_connectionSlots.Wait(0))
        {
            ElsieMetrics.ConnectionsRejected.Add(1);
            _logger.LogWarning("Rejecting connection: max concurrent connections reached.");
            try
            {
                socket.Dispose();
            }
            catch
            {
                // ignore
            }

            return;
        }

        if (applyTcpOptions)
        {
            socket.NoDelay = true;
            ApplySocketOptions(socket);
        }

        ElsieMetrics.ActiveConnections.Add(1);
        var handler = new ConnectionHandler(
            _services,
            _dispatcher,
            _features,
            options,
            _serverOptions,
            _log,
            _logger,
            _loggerFactory);

        var id = Guid.NewGuid();
        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var task = Task.Run(async () =>
        {
            try
            {
                await handler.RunAsync(socket, connectionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown / disconnect
            }
            catch
            {
                try { socket.Dispose(); } catch { /* ignore */ }
            }
            finally
            {
                ElsieMetrics.ActiveConnections.Add(-1);
                _connectionSlots.Release();
                if (_connections.TryRemove(id, out var removed))
                {
                    try { removed.ConnectionCts.Dispose(); } catch { /* ignore */ }
                }
            }
        }, CancellationToken.None);

        _connections[id] = new ConnectionEntry(socket, connectionCts, task);
    }

    private void ApplySocketOptions(Socket socket)
    {
        if (!_serverOptions.TcpKeepAlive)
        {
            return;
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            var timeMs = (int)Math.Clamp(_serverOptions.TcpKeepAliveTime.TotalMilliseconds, 1, int.MaxValue);
            var intervalMs = (int)Math.Clamp(_serverOptions.TcpKeepAliveInterval.TotalMilliseconds, 1, int.MaxValue);

            if (OperatingSystem.IsWindows())
            {
                var bytes = new byte[12];
                BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (uint)1);
                BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), (uint)timeMs);
                BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), (uint)intervalMs);
                socket.IOControl(IOControlCode.KeepAliveValues, bytes, null);
            }
            else if (OperatingSystem.IsLinux())
            {
                const int solTcp = 6;
                const int tcpKeepIdle = 4;
                const int tcpKeepIntvl = 5;
                var idleSec = Math.Max(1, timeMs / 1000);
                var intvlSec = Math.Max(1, intervalMs / 1000);
                socket.SetRawSocketOption(solTcp, tcpKeepIdle, BitConverter.GetBytes(idleSec));
                socket.SetRawSocketOption(solTcp, tcpKeepIntvl, BitConverter.GetBytes(intvlSec));
            }
        }
        catch
        {
            // Keepalive tuning is best-effort.
        }
    }

    private static string FormatHost(IPEndPoint ep)
    {
        if (ep.Address.Equals(IPAddress.Any) || ep.Address.Equals(IPAddress.IPv6Any))
        {
            return $"localhost:{ep.Port}";
        }

        if (ep.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return $"[{ep.Address}]:{ep.Port}";
        }

        return $"{ep.Address}:{ep.Port}";
    }

    private sealed class ConnectionEntry
    {
        public ConnectionEntry(Socket socket, CancellationTokenSource connectionCts, Task task)
        {
            Socket = socket;
            ConnectionCts = connectionCts;
            Task = task;
        }

        public Socket Socket { get; }
        public CancellationTokenSource ConnectionCts { get; }
        public Task Task { get; }
    }
}
