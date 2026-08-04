using System.Net.Sockets;

namespace Elsie.Web.Hosting;

/// <summary>
/// Polls a socket with Peek while a handler runs; cancels when the client disconnects.
/// Does not consume pipelined bytes. A graceful FIN (half-close) does NOT cancel (the peer
/// may still be reading the response), but a subsequent RST / socket error still fires.
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
        // Hoisted out of the loop: the two-sample FIN/RST discrimination below requires
        // these to persist across iterations (declared inside, emptyReads never reaches 2
        // and the peek is dead code).
        var peekBuf = new byte[1];
        var emptyReads = 0;
        try
        {
            while (!_cts.IsCancellationRequested && !_serverToken.IsCancellationRequested)
            {
                try
                {
                    // A socket error (RST / reset) signals a real disconnect — even after a
                    // graceful FIN (half-close). (Probe: FIN leaves SelectError false; RST sets
                    // it true.) Cancel so RequestAborted fires and the handler unwinds.
                    if (_socket.Poll(0, SelectMode.SelectError))
                    {
                        _cts.Cancel();
                        return;
                    }

                    // Readable with no bytes buffered MIGHT be a completed receive (FIN/RST)
                    // or a race: the handler drained bytes between our Poll and Available
                    // check. Discriminate by requiring two consecutive readable+empty samples
                    // 50ms apart — a live connection with in-flight data cannot stay readable
                    // with zero available; only a pending EOF/error completion can. Only then
                    // peek (error-code overload, returns immediately: the completion is
                    // pending; a bare peek on the first sample could block and wedge the
                    // connection — that bug bit twice already).
                    if (_socket.Poll(0, SelectMode.SelectRead) && _socket.Available == 0)
                    {
                        emptyReads++;
                        if (emptyReads >= 2)
                        {
                            var n = _socket.Receive(peekBuf, 0, 1, SocketFlags.Peek, out var errorCode);
                            if (errorCode != SocketError.Success && errorCode != SocketError.WouldBlock)
                            {
                                // RST (also RST-after-FIN, which Windows does not surface via
                                // SelectError) — real disconnect.
                                try { _cts.Cancel(); } catch (ObjectDisposedException) { /* ignore */ }
                                return;
                            }

                            if (n == 1)
                            {
                                // Data raced in after all — not a close.
                                emptyReads = 0;
                            }
                            // n == 0: graceful FIN (half-close). The peer can still be
                            // reading, so aborting the in-flight request would be a false
                            // positive that drops the response. Keep watching so a later RST
                            // still cancels RequestAborted instead of silently stopping at FIN.
                        }
                    }
                    else
                    {
                        emptyReads = 0;
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
