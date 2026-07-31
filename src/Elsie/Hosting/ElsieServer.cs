using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Elsie.Web.Hosting;

internal sealed class ElsieServer : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly IReadOnlyList<ElsieListenOptions> _endpoints;
    private readonly ElsieServerOptions _serverOptions;
    private readonly Action<string>? _log;
    private readonly ILogger _logger;
    private readonly List<TcpListener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _acceptLoops = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();
    private readonly SemaphoreSlim _connectionSlots;
    private int _started;

    public ElsieServer(
        ServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        IReadOnlyList<ElsieListenOptions> endpoints,
        ElsieServerOptions serverOptions,
        Action<string>? log,
        ILoggerFactory? loggerFactory = null)
    {
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _endpoints = endpoints;
        _serverOptions = serverOptions ?? new ElsieServerOptions();
        _log = log;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Elsie.Server");
        var max = Math.Max(1, _serverOptions.MaxConcurrentConnections);
        _connectionSlots = new SemaphoreSlim(max, max);
    }

    public IReadOnlyList<IPEndPoint> BoundEndpoints { get; private set; } = Array.Empty<IPEndPoint>();

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
        }

        BoundEndpoints = bound;
        return Task.CompletedTask;
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

        try
        {
            await Task.WhenAll(_acceptLoops).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        // Drain in-flight connections
        var pending = _connections.Keys.ToArray();
        if (pending.Length > 0)
        {
            var drain = Task.WhenAll(pending);
            var completed = await Task.WhenAny(drain, Task.Delay(_serverOptions.ConnectionDrainTimeout))
                .ConfigureAwait(false);
            if (completed != drain)
            {
                _logger.LogWarning("Connection drain timed out with {Count} still active.", pending.Length);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _connectionSlots.Dispose();
        await _services.DisposeAsync().ConfigureAwait(false);
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

                continue;
            }

            socket.NoDelay = true;
            ElsieMetrics.ActiveConnections.Add(1);
            var handler = new ConnectionHandler(
                _services,
                _dispatcher,
                _features,
                options,
                _serverOptions,
                _log,
                _logger);

            var task = Task.Run(async () =>
            {
                try
                {
                    await handler.RunAsync(socket, ct).ConfigureAwait(false);
                }
                catch
                {
                    try { socket.Dispose(); } catch { /* ignore */ }
                }
                finally
                {
                    ElsieMetrics.ActiveConnections.Add(-1);
                    _connectionSlots.Release();
                }
            }, CancellationToken.None);

            _connections[task] = 0;
            _ = task.ContinueWith(
                t => _connections.TryRemove(t, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
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
}
