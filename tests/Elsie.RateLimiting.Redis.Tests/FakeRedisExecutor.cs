using Elsie.Extensions.RateLimiting.Redis;
using StackExchange.Redis;

namespace Elsie.RateLimiting.Redis.Tests;

/// <summary>
/// Hand-rolled fake of the internal <see cref="IRedisRateLimitExecutor"/> seam
/// used by the Redis rate-limit stores. Records the script + key + args of every
/// call and returns canned results (or throws to simulate outages).
/// </summary>
internal sealed class FakeRedisExecutor : IRedisRateLimitExecutor
{
    private readonly Func<string, RedisKey, RedisValue[], Task<RedisResult>> _handler;

    public FakeRedisExecutor(Func<string, RedisKey, RedisValue[], Task<RedisResult>> handler)
    {
        _handler = handler;
    }

    public List<(string Script, RedisKey Key, RedisValue[] Args)> Calls { get; } = [];

    public Task<RedisResult> EvaluateAsync(string script, RedisKey key, RedisValue[] args)
    {
        Calls.Add((script, key, args));
        return _handler(script, key, args);
    }

    /// <summary>Executor that returns a canned array result for every call.</summary>
    public static FakeRedisExecutor Returning(params RedisValue[] values) =>
        new((_, _, _) => Task.FromResult(RedisResult.Create(values)));

    /// <summary>Executor that throws a Redis connection error (simulated outage).</summary>
    public static FakeRedisExecutor ThrowingConnectionError() =>
        new((_, _, _) => Task.FromException<RedisResult>(
            new RedisConnectionException(
                ConnectionFailureType.UnableToResolvePhysicalConnection,
                CommandFlags.None,
                "simulated outage",
                innerException: null,
                commandStatus: CommandStatus.Unknown)));

    /// <summary>Executor that never completes (simulated per-op timeout).</summary>
    public static FakeRedisExecutor Hanging() =>
        new((_, _, _) => Task.Delay(Timeout.InfiniteTimeSpan).ContinueWith(
            _ => RedisResult.Create(RedisValue.Null), TaskScheduler.Default));
}
