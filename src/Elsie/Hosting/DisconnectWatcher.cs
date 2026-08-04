using System.Net.Sockets;

namespace Elsie.Web.Hosting;

/// <summary>
/// Polls a socket with Peek while a handler runs; cancels when the client disconnects.
/// Does not consume pipelined bytes.
/// </summary>
internal sealed class DisconnectWatcher : IDisposable
{
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _serverToken;
    private int _started;
    private int _disposed;
    private Task? _loop;

    public DisconnectWatcher(Socket socket, CancellationToken serverToken)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _serverToken = serverToken;
    }

    public CancellationToken Token => _cts.Token;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _loop = Task.Run(PollLoopAsync, CancellationToken.None);
    }

    public void Stop()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    private async Task PollLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested && !_serverToken.IsCancellationRequested)
            {
                try
                {
                    // Non-blocking only: a blocking Peek-receive here races the connection
                    // handler's own body reads (bytes drained between Poll and Receive),
                    // wedging the watcher thread and, via Dispose, the whole connection.
                    if (_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0)
                    {
                        // Readable with no bytes = graceful FIN (half-close). The peer can
                        // still be reading, so aborting the in-flight request would be a
                        // false positive that drops the response. Stop watching instead of
                        // spinning (EOF stays readable forever); a real disconnect (RST /
                        // disposal) surfaces as SocketException/ObjectDisposedException, and
                        // the response write itself reports a vanished peer.
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    try { _cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
                    return;
                }
                catch (SocketException)
                {
                    try { _cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
                    return;
                }

                try
                {
                    await Task.Delay(50, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal stop
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
        try
        {
            _loop?.GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        _cts.Dispose();
    }
}
