using System.Globalization;

namespace Elsie;

/// <summary>
/// Fluent builder for a <c>Cache-Control</c> response header value (RFC 9111 §5.2).
/// Directives serialize in a fixed canonical order; <see cref="ToString"/> returns the header value.
/// </summary>
public sealed class ElsieCacheControl
{
    private bool _isPublic;
    private bool _isPrivate;
    private bool _noStore;
    private bool _noCache;
    private bool _mustRevalidate;
    private bool _immutable;
    private TimeSpan? _maxAge;
    private TimeSpan? _sharedMaxAge;

    /// <summary>Add the <c>public</c> directive (response may be cached by shared caches).</summary>
    public ElsieCacheControl Public()
    {
        _isPublic = true;
        return this;
    }

    /// <summary>Add the <c>private</c> directive (shared caches must not cache the response).</summary>
    public ElsieCacheControl Private()
    {
        _isPrivate = true;
        return this;
    }

    /// <summary>Add the <c>no-store</c> directive (caches must not store anything).</summary>
    public ElsieCacheControl NoStore()
    {
        _noStore = true;
        return this;
    }

    /// <summary>Add the <c>no-cache</c> directive (revalidate with the origin before reuse).</summary>
    public ElsieCacheControl NoCache()
    {
        _noCache = true;
        return this;
    }

    /// <summary>Add the <c>must-revalidate</c> directive (stale responses must not be reused).</summary>
    public ElsieCacheControl MustRevalidate()
    {
        _mustRevalidate = true;
        return this;
    }

    /// <summary>Add the <c>immutable</c> directive (no revalidation needed while fresh).</summary>
    public ElsieCacheControl Immutable()
    {
        _immutable = true;
        return this;
    }

    /// <summary>Set <c>max-age</c> — freshness lifetime in whole seconds.</summary>
    public ElsieCacheControl MaxAge(TimeSpan value)
    {
        _maxAge = CheckDeltaSeconds(value);
        return this;
    }

    /// <summary>Set <c>s-maxage</c> — shared-cache freshness lifetime in whole seconds.</summary>
    public ElsieCacheControl SharedMaxAge(TimeSpan value)
    {
        _sharedMaxAge = CheckDeltaSeconds(value);
        return this;
    }

    /// <summary>Serialize the configured directives into a <c>Cache-Control</c> header value.</summary>
    /// <exception cref="InvalidOperationException">When both public and private are set, or no directive was configured.</exception>
    public override string ToString()
    {
        if (_isPublic && _isPrivate)
        {
            throw new InvalidOperationException("Cache-Control cannot be both public and private.");
        }

        var parts = new List<string>(7);
        if (_noStore)
        {
            parts.Add("no-store");
        }

        if (_noCache)
        {
            parts.Add("no-cache");
        }

        if (_isPrivate)
        {
            parts.Add("private");
        }

        if (_isPublic)
        {
            parts.Add("public");
        }

        if (_mustRevalidate)
        {
            parts.Add("must-revalidate");
        }

        if (_maxAge is { } maxAge)
        {
            parts.Add("max-age=" + ((int)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        if (_sharedMaxAge is { } sharedMaxAge)
        {
            parts.Add("s-maxage=" + ((int)sharedMaxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        if (_immutable)
        {
            parts.Add("immutable");
        }

        if (parts.Count == 0)
        {
            throw new InvalidOperationException("No Cache-Control directives configured.");
        }

        return string.Join(", ", parts);
    }

    private static TimeSpan CheckDeltaSeconds(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Cache-Control delta-seconds must be non-negative.");
        }

        return value;
    }
}

/// <summary>Cache-Control helpers for <see cref="ElsieResult"/>.</summary>
public static class ElsieResultCacheExtensions
{
    /// <summary>
    /// Set the <c>Cache-Control</c> response header (replaces any existing value) from a fluent
    /// directive builder:
    /// <code>result.WithCacheControl(c => c.Public().MaxAge(TimeSpan.FromMinutes(5)))</code>
    /// </summary>
    public static ElsieResult WithCacheControl(this ElsieResult result, Action<ElsieCacheControl> configure)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(configure);
        var directives = new ElsieCacheControl();
        configure(directives);
        return result.WithHeader("Cache-Control", directives.ToString());
    }
}
