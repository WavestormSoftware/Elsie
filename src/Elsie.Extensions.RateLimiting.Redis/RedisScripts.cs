namespace Elsie.Extensions.RateLimiting.Redis;

/// <summary>
/// Loads the embedded Lua scripts shipped with the package.
/// </summary>
internal static class RedisScripts
{
    public static string FixedWindow { get; } = Load("fixed_window.lua");

    public static string SlidingWindow { get; } = Load("sliding_window.lua");

    public static string TokenBucket { get; } = Load("token_bucket.lua");

    private static string Load(string name)
    {
        var resource = $"Elsie.Extensions.RateLimiting.Redis.Resources.{name}";
        using var stream = typeof(RedisScripts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded Lua resource '{resource}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
