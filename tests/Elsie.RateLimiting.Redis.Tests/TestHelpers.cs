using System.Globalization;
using Elsie.Extensions.RateLimiting.Redis;
using StackExchange.Redis;

namespace Elsie.RateLimiting.Redis.Tests;

/// <summary>Injectable clock for exercising reset/retry math.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utc;

    public ManualTimeProvider(DateTimeOffset utc) => _utc = utc.ToUniversalTime();

    public override DateTimeOffset GetUtcNow() => _utc;

    public void Advance(TimeSpan by) => _utc += by;
}

internal static class TestHelpers
{
    public static long NowUnixSeconds(DateTimeOffset utc) => utc.ToUnixTimeSeconds();

    public static DateTimeOffset FixedNow() =>
        DateTimeOffset.Parse("2024-05-01T12:00:00Z", CultureInfo.InvariantCulture);
}
