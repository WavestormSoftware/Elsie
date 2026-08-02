using Elsie.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Elsie.Extensions.RateLimiting.Redis;

/// <summary>
/// Minimal Redis command surface used by the rate-limit stores. Exists so unit
/// tests can drive the stores with a hand-rolled fake instead of a live Redis.
/// </summary>
internal interface IRedisRateLimitExecutor
{
    Task<RedisResult> EvaluateAsync(string script, RedisKey key, RedisValue[] args);
}

internal sealed class MultiplexerExecutor : IRedisRateLimitExecutor
{
    private readonly IConnectionMultiplexer _connection;

    public MultiplexerExecutor(IConnectionMultiplexer connection) => _connection = connection;

    public async Task<RedisResult> EvaluateAsync(string script, RedisKey key, RedisValue[] args)
    {
        var database = _connection.GetDatabase();
        return await database.ScriptEvaluateAsync(script, [key], args).ConfigureAwait(false);
    }
}

/// <summary>
/// Base class for Redis-backed <see cref="IRateLimitStore"/> implementations.
/// Handles key prefixing, the per-operation timeout, and the fail-open / fail-closed
/// outage policy (default: fail open with a warning).
/// </summary>
public abstract class RedisRateLimitStore : IRateLimitStore, IDisposable, IAsyncDisposable
{
    private readonly IRedisRateLimitExecutor _executor;
    private readonly RedisRateLimitOptions _options;
    private readonly ILogger? _logger;
    private IConnectionMultiplexer? _ownedConnection;

    /// <summary>
    /// Creates a store over an existing multiplexer. The caller keeps ownership of the
    /// multiplexer; this store will not dispose it.
    /// </summary>
    protected RedisRateLimitStore(
        IConnectionMultiplexer connection,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
        : this(connection, options, logger, ownsConnection: false)
    {
    }

    /// <summary>Test seam constructor (no live Redis required).</summary>
    internal RedisRateLimitStore(
        IRedisRateLimitExecutor executor,
        RedisRateLimitOptions? options = null,
        ILogger? logger = null)
        : this(executor, options, logger, ownedConnection: null)
    {
    }

    protected RedisRateLimitStore(
        IConnectionMultiplexer connection,
        RedisRateLimitOptions? options,
        ILogger? logger,
        bool ownsConnection)
        : this(
            new MultiplexerExecutor(connection ?? throw new ArgumentNullException(nameof(connection))),
            options,
            logger,
            ownedConnection: ownsConnection ? connection : null)
    {
    }

    private RedisRateLimitStore(
        IRedisRateLimitExecutor executor,
        RedisRateLimitOptions? options,
        ILogger? logger,
        IConnectionMultiplexer? ownedConnection)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _options = options ?? new RedisRateLimitOptions();
        _logger = logger;
        _ownedConnection = ownedConnection;
    }

    /// <summary>The Lua script executed for every operation of this store.</summary>
    protected abstract string Script { get; }

    /// <inheritdoc />
    public abstract bool TryAcquire(string key, out TimeSpan retryAfter);

    /// <inheritdoc />
    public abstract bool TryPeek(string key, out RateLimitCounters counters);

    /// <summary>
    /// Connects a new multiplexer for the given Redis connection string. The caller
    /// (a <c>Create</c> factory) owns the resulting connection.
    /// </summary>
    protected static IConnectionMultiplexer Connect(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return ConnectionMultiplexer.Connect(connectionString);
    }

    /// <summary>
    /// Evaluates the store script for <paramref name="key"/>, applying the key prefix
    /// and the per-operation timeout.
    /// </summary>
    protected async Task<RedisResult> EvaluateScriptAsync(string key, params RedisValue[] args)
    {
        var redisKey = new RedisKey(string.Concat(_options.KeyPrefix, key));
        return await _executor.EvaluateAsync(Script, redisKey, args)
            .WaitAsync(TimeSpan.FromMilliseconds(_options.OperationTimeoutMilliseconds))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the configured outage policy after a Redis failure. Returns the
    /// <c>TryAcquire</c> verdict (true = allow). When fail-closed, suggests
    /// <see cref="RedisRateLimitOptions.FailClosedRetryAfter"/>.
    /// </summary>
    protected bool OnOutage(Exception exception, out TimeSpan retryAfter)
    {
        _logger?.LogWarning(
            exception,
            "Redis rate-limit store unreachable (timeout {Timeout}ms); applying {OutageMode} outage policy.",
            _options.OperationTimeoutMilliseconds,
            _options.OutageMode);

        if (_options.OutageMode == RedisOutageMode.FailClosed)
        {
            retryAfter = _options.FailClosedRetryAfter;
            return false;
        }

        retryAfter = TimeSpan.Zero;
        return true;
    }

    /// <summary>
    /// True when <paramref name="exception"/> represents a Redis availability problem
    /// (connection failure, per-operation timeout) rather than a script/usage error.
    /// </summary>
    protected static bool IsOutage(Exception exception) =>
        exception is RedisConnectionException or RedisTimeoutException or TimeoutException or OperationCanceledException;

    /// <inheritdoc />
    public void Dispose()
    {
        var connection = Interlocked.Exchange(ref _ownedConnection, null);
        connection?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _ownedConnection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }
}
