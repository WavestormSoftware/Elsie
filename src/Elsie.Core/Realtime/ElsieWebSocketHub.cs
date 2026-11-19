using System.Collections.Concurrent;

namespace Elsie.Realtime;

/// <summary>
/// A set of WebSocket connections addressed together (a room / topic). Sends are serialized per
/// socket by <see cref="ElsieWebSocket"/>, so broadcasting from several threads is safe. Closed
/// connections are dropped automatically.
/// </summary>
public sealed class ElsieWebSocketGroup
{
    private readonly object _gate = new();
    private readonly HashSet<ElsieWebSocket> _sockets = new(ReferenceEqualityComparer.Instance);

    /// <summary>Live connection count.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sockets.Count;
            }
        }
    }

    /// <summary>Add a connection (no-op when already present or closed).</summary>
    public bool Add(ElsieWebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (socket.IsClosed)
        {
            return false;
        }

        lock (_gate)
        {
            return _sockets.Add(socket);
        }
    }

    /// <summary>Remove a connection.</summary>
    public bool Remove(ElsieWebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        lock (_gate)
        {
            return _sockets.Remove(socket);
        }
    }

    /// <summary>Snapshot of the current connections.</summary>
    public IReadOnlyList<ElsieWebSocket> Snapshot()
    {
        lock (_gate)
        {
            return _sockets.ToArray();
        }
    }

    /// <summary>Send a text message to every live connection. Returns the number of sends.</summary>
    public Task<int> BroadcastTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return BroadcastAsync(static (socket, payload, ct) => socket.SendTextAsync(payload!, ct), text, cancellationToken);
    }

    /// <summary>Send a binary message to every live connection. Returns the number of sends.</summary>
    public Task<int> BroadcastBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var payload = data.ToArray();
        return BroadcastAsync(static (socket, bytes, ct) => socket.SendBinaryAsync(bytes, ct), payload, cancellationToken);
    }

    private async Task<int> BroadcastAsync<T>(
        Func<ElsieWebSocket, T, CancellationToken, Task> send,
        T payload,
        CancellationToken cancellationToken)
    {
        var targets = Snapshot();
        if (targets.Count == 0)
        {
            return 0;
        }

        var sent = 0;
        var tasks = new List<Task>(targets.Count);
        foreach (var socket in targets)
        {
            if (socket.IsClosed)
            {
                Remove(socket);
                continue;
            }

            tasks.Add(DeliverAsync(socket, send, payload, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return sent;

        async Task DeliverAsync(ElsieWebSocket socket, Func<ElsieWebSocket, T, CancellationToken, Task> send, T payload, CancellationToken ct)
        {
            try
            {
                await send(socket, payload, ct).ConfigureAwait(false);
                Interlocked.Increment(ref sent);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                Remove(socket);
            }
        }
    }
}

/// <summary>
/// Named groups of live WebSocket connections. Register as a singleton and use
/// <see cref="Join"/> inside a <c>ElsieResult.WebSocket(...)</c> handler to keep membership scoped
/// to the connection.
/// </summary>
public sealed class ElsieWebSocketHub
{
    private readonly ConcurrentDictionary<string, ElsieWebSocketGroup> _groups = new(StringComparer.Ordinal);

    /// <summary>Names of the rooms that currently have at least one connection.</summary>
    public IReadOnlyList<string> Rooms =>
        _groups.Where(static pair => pair.Value.Count > 0).Select(static pair => pair.Key).ToArray();

    /// <summary>Get (creating when needed) the group for a room.</summary>
    public ElsieWebSocketGroup GetOrCreate(string room)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        return _groups.GetOrAdd(room, static _ => new ElsieWebSocketGroup());
    }

    /// <summary>Get a room group, or null when it was never used.</summary>
    public ElsieWebSocketGroup? Get(string room)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        return _groups.TryGetValue(room, out var group) ? group : null;
    }

    /// <summary>
    /// Add <paramref name="socket"/> to a room. Dispose the returned lease (e.g. with
    /// <c>await using</c>) to leave the room when the connection ends.
    /// </summary>
    public IDisposable Join(string room, ElsieWebSocket socket)
    {
        var group = GetOrCreate(room);
        group.Add(socket);
        return new Membership(group, socket);
    }

    /// <summary>Broadcast a text message to one room (0 when the room is empty).</summary>
    public Task<int> BroadcastAsync(string room, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        ArgumentNullException.ThrowIfNull(text);
        return Get(room)?.BroadcastTextAsync(text, cancellationToken) ?? Task.FromResult(0);
    }

    /// <summary>Broadcast binary data to one room.</summary>
    public Task<int> BroadcastAsync(string room, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        return Get(room)?.BroadcastBinaryAsync(data, cancellationToken) ?? Task.FromResult(0);
    }

    /// <summary>Broadcast a text message to every live connection.</summary>
    public async Task<int> BroadcastAllAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var total = 0;
        foreach (var group in _groups.Values)
        {
            total += await group.BroadcastTextAsync(text, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private sealed class Membership(ElsieWebSocketGroup group, ElsieWebSocket socket) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                group.Remove(socket);
            }
        }
    }
}
