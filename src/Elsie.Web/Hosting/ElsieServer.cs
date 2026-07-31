using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web.Hosting;

internal sealed class ElsieServer : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly ElsieDispatcher _dispatcher;
    private readonly ElsieServerFeatures _features;
    private readonly IReadOnlyList<ElsieListenOptions> _endpoints;
    private readonly Action<string>? _log;
    private readonly List<TcpListener> _listeners = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _acceptLoops = new();
    private int _started;

    public ElsieServer(
        ServiceProvider services,
        ElsieDispatcher dispatcher,
        ElsieServerFeatures features,
        IReadOnlyList<ElsieListenOptions> endpoints,
        Action<string>? log)
    {
        _services = services;
        _dispatcher = dispatcher;
        _features = features;
        _endpoints = endpoints;
        _log = log;
    }

    public IReadOnlyList<IPEndPoint> BoundEndpoints { get; private set; } = Array.Empty<IPEndPoint>();

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("Server already started.");
        }

        // Warm route table + OpenAPI
        var routes = _services.GetRequiredService<Routing.RouteTable>();
        _features.WarmOpenApi(routes);

        var bound = new List<IPEndPoint>();
        foreach (var ep in _endpoints)
        {
            var listener = new TcpListener(ep.Address, ep.Port);
            listener.Start();
            var local = (IPEndPoint)listener.LocalEndpoint;
            bound.Add(local);
            _listeners.Add(listener);
            var scheme = ep.UseHttps ? "https" : "http";
            _log?.Invoke($"Listening on {scheme}://{FormatHost(local)}/");
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
            // normal shutdown
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
            // ignore accept cancellations
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
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

            socket.NoDelay = true;
            var handler = new ConnectionHandler(_services, _dispatcher, _features, options, _log);
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler.RunAsync(socket, ct).ConfigureAwait(false);
                }
                catch
                {
                    try { socket.Dispose(); } catch { /* ignore */ }
                }
            }, ct);
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
