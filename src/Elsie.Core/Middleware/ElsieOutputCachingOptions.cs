namespace Elsie.Middleware;

/// <summary>Options controlling the in-memory output cache middleware.</summary>
public sealed class ElsieOutputCachingOptions
{
    /// <summary>Max entries in the in-memory LRU (default 1024).</summary>
    public int MaxEntries { get; set; } = 1024;

    /// <summary>Max total body bytes held by the cache (default 64 MiB).</summary>
    public long MaxCacheBytes { get; set; } = 64L * 1024 * 1024;
}
