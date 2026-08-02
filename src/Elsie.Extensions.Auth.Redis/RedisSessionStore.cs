using Elsie.Auth;
using StackExchange.Redis;

namespace Elsie.Extensions.Auth.Redis;

/// <summary>
/// Server-side session store for Elsie cookie auth backed by Redis
/// (StackExchange.Redis). Sessions are stored under <c>elsie:session:{id}</c>
/// with a sliding TTL: reads return the payload and the auth package re-stores it
/// with a fresh TTL on every use; writes apply the supplied TTL directly.
/// </summary>
public sealed class RedisSessionStore : IElsieSessionStore, IAsyncDisposable, IDisposable
{
    private readonly IDatabase _database;
    private readonly RedisSessionStoreOptions _options;
    private readonly IConnectionMultiplexer? _ownedConnection;

    /// <summary>
    /// Creates a store over an existing multiplexer. The caller keeps ownership; the
    /// store will not dispose the multiplexer.
    /// </summary>
    public RedisSessionStore(IConnectionMultiplexer connection, RedisSessionStoreOptions? options = null)
        : this(connection ?? throw new ArgumentNullException(nameof(connection)), options, ownsConnection: false)
    {
    }

    /// <summary>Creates a store over a new multiplexer built from <paramref name="connectionString"/>.</summary>
    public static async Task<RedisSessionStore> ConnectAsync(
        string connectionString,
        RedisSessionStoreOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var connection = await ConnectionMultiplexer.ConnectAsync(connectionString).ConfigureAwait(false);
        return new RedisSessionStore(connection, options, ownsConnection: true);
    }

    private RedisSessionStore(IConnectionMultiplexer connection, RedisSessionStoreOptions? options, bool ownsConnection)
    {
        _database = connection.GetDatabase();
        _options = options ?? new RedisSessionStoreOptions();
        _ownedConnection = ownsConnection ? connection : null;
    }

    /// <inheritdoc />
    public Task SetAsync(
        string sessionId,
        byte[] payload,
        TimeSpan slidingTtl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(payload);

        var key = KeyFor(sessionId);
        return WithTimeout(
            async ct =>
            {
                var done = await _database.StringSetAsync(key, payload, slidingTtl, When.Always).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return done;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.FromResult<byte[]?>(null);
        }

        var key = KeyFor(sessionId);
        return WithTimeout(
            async ct =>
            {
                var value = await _database.StringGetAsync(key).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return value.IsNull ? null : (byte[]?)value!;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Task.CompletedTask;
        }

        var key = KeyFor(sessionId);
        return WithTimeout(
            async ct =>
            {
                var done = await _database.KeyDeleteAsync(key).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return done;
            },
            cancellationToken);
    }

    /// <summary>Redis key for a session id.</summary>
    public string KeyFor(string sessionId) => _options.NormalizedPrefix() + sessionId;

    /// <summary>Number of live session keys (test/diagnostics; requires admin for KEYS).</summary>
    internal string[] ListKeys(IConnectionMultiplexer connection)
    {
        var server = connection.GetServer(connection.GetEndPoints()[0]);
        return server.Keys(pattern: _options.NormalizedPrefix() + "*")
            .Select(k => (string)k!)
            .ToArray();
    }

    private async Task<T> WithTimeout<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(PerOperationTimeout());
        try
        {
            return await operation(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Elsie session store operation exceeded {PerOperationTimeout().TotalMilliseconds} ms.");
        }
    }

    private TimeSpan PerOperationTimeout()
    {
        var ms = _options.OperationTimeoutMilliseconds > 0
            ? _options.OperationTimeoutMilliseconds
            : 100;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <inheritdoc />
    public void Dispose() => _ownedConnection?.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownedConnection is IAsyncDisposable asyncDisp)
        {
            return asyncDisp.DisposeAsync();
        }

        _ownedConnection?.Dispose();
        return ValueTask.CompletedTask;
    }
}
