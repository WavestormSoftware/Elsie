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
        var buf = new byte[1];
        try
        {
            while (!_cts.IsCancellationRequested && !_serverToken.IsCancellationRequested)
            {
                try
                {
                    // Readable + Peek 0 bytes ⇒ peer half-closed (FIN).
                    if (_socket.Poll(0, SelectMode.SelectRead))
                    {
                        var n = _socket.Receive(buf, 0, 1, SocketFlags.Peek);
                        if (n == 0)
                        {
                            // A graceful FIN only means the peer will send no more bytes. The
                            // peer can still be reading (half-close, e.g. a client that
                            // shutdown(SHUT_WR) after sending the full request), so aborting
                            // the in-flight request here is a false positive that drops the
                            // response. A real disconnect (RST / socket error / disposal)
                            // surfaces as a SocketException/ObjectDisposedException below;
                            // otherwise keep polling until the handler finishes — the
                            // response write itself reports a vanished peer.
                            continue;
                        }
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
