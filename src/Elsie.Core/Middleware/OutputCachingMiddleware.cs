using System.Collections.Concurrent;
using System.Globalization;

namespace Elsie.Middleware;

/// <summary>
/// Opt-in in-memory output cache middleware. Caches successful GET/HEAD responses keyed by
/// method + route path + query string + <c>Accept-Encoding</c> (so pre-compressed variants are
/// memoized independently). Honors <c>Cache-Control: no-store</c>/<c>no-cache</c> on the request
/// and the response, and composes with <see cref="ElsieResultConditionalGetExtensions.WithETag"/>
/// so a cached response is served as 304 when the request's <c>If-None-Match</c> matches the
/// stored ETag. Only buffered (non-streaming) 200 responses are cached.
/// </summary>
public sealed class OutputCachingMiddleware : IElsieMiddleware
{
    private readonly ElsieOutputCachingOptions _options;
    private readonly OutputCacheStore _store;

    /// <summary>Create the middleware (DI; see <c>AddOutputCaching</c>).</summary>
    public OutputCachingMiddleware(ElsieOutputCachingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _store = new OutputCacheStore(
            Math.Max(1, options.MaxEntries),
            Math.Max(1, options.MaxCacheBytes));
    }

    /// <inheritdoc />
    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var method = context.Request.Method;
        if (method is not ("GET" or "HEAD"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Request-level Cache-Control: no-store / no-cache means the response must not be
        // served from (or stored into) the cache.
        if (IsNoStoreOrNoCache(context.Request.GetHeader("Cache-Control")))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var key = BuildKey(context, method);
        if (_store.TryGet(key, out var cached) && cached is not null)
        {
            var rebuilt = cached.ToResult();
            context.Result = rebuilt.EvaluateConditional(context.Request);
            return;
        }

        await next(context).ConfigureAwait(false);

        var result = context.Result;
        if (result is null || !IsCacheable(result))
        {
            return;
        }

        _store.Add(key, CachedEntry.FromResult(result));
    }

    /// <summary>Cache key: method + path + query + Accept-Encoding.</summary>
    private static string BuildKey(ElsieContext context, string method)
    {
        var acceptEncoding = context.Request.GetHeader("Accept-Encoding") ?? string.Empty;
        return string.Concat(
            method,
            "\n",
            context.Request.Path,
            "\n",
            context.Request.QueryString,
            "\n",
            acceptEncoding);
    }

    private static bool IsNoStoreOrNoCache(string? cacheControl)
    {
        if (string.IsNullOrWhiteSpace(cacheControl))
        {
            return false;
        }

        foreach (var raw in cacheControl.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var directive = raw.Split('=', 2)[0].Trim();
            if (directive.Equals("no-store", StringComparison.OrdinalIgnoreCase) ||
                directive.Equals("no-cache", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A response is cacheable when it is a buffered 200 with no no-store/no-cache.</summary>
    private static bool IsCacheable(ElsieResult result)
    {
        if (result.StatusCode != 200 || result.Body is null || result.BodyWriter is not null)
        {
            return false;
        }

        if (result.WebSocketHandler is not null)
        {
            return false;
        }

        return !IsNoStoreOrNoCache(result.Headers.GetSingle("Cache-Control"));
    }

    /// <summary>Typed view over the response stored in the cache.</summary>
    private sealed class CachedEntry
    {
        public required int StatusCode { get; init; }
        public required string? ContentType { get; init; }
        public required byte[] Body { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; init; }

        public static CachedEntry FromResult(ElsieResult result)
        {
            var headers = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in result.Headers)
            {
                headers[name] = values.ToArray();
            }

            return new CachedEntry
            {
                StatusCode = result.StatusCode,
                ContentType = result.ContentType,
                Body = result.Body!.Value.ToArray(),
                Headers = headers
            };
        }

        public ElsieResult ToResult()
        {
            var result = ElsieResult.Bytes(Body, ContentType ?? "application/octet-stream", StatusCode);
            foreach (var (name, values) in Headers)
            {
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var value in values)
                {
                    result = result.WithHeader(name, value);
                }
            }

            return result;
        }
    }

    /// <summary>Thread-safe LRU cache bounded by entry count and total body bytes.</summary>
    private sealed class OutputCacheStore
    {
        private readonly int _maxEntries;
        private readonly long _maxBytes;
        private readonly object _gate = new();
        private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, CachedEntry>>> _map = new(StringComparer.Ordinal);
        private readonly LinkedList<KeyValuePair<string, CachedEntry>> _lru = new();
        private long _totalBytes;

        public OutputCacheStore(int maxEntries, long maxBytes)
        {
            _maxEntries = maxEntries;
            _maxBytes = maxBytes;
        }

        public bool TryGet(string key, out CachedEntry? entry)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    entry = node.Value.Value;
                    return true;
                }

                entry = null;
                return false;
            }
        }

        public void Add(string key, CachedEntry entry)
        {
            lock (_gate)
            {
                // Remove an existing entry for the same key before adding (refresh).
                if (_map.TryGetValue(key, out var existing))
                {
                    _lru.Remove(existing);
                    _map.Remove(key);
                    _totalBytes -= existing.Value.Value.Body.Length;
                }

                var node = _lru.AddFirst(new KeyValuePair<string, CachedEntry>(key, entry));
                _map[key] = node;
                _totalBytes += entry.Body.Length;

                // Evict least-recently-used until back under both bounds.
                while (_map.Count > _maxEntries || _totalBytes > _maxBytes)
                {
                    var last = _lru.Last;
                    if (last is null)
                    {
                        break;
                    }

                    _lru.RemoveLast();
                    _map.Remove(last.Value.Key);
                    _totalBytes -= last.Value.Value.Body.Length;
                }
            }
        }
    }
}

/// <summary>First-wins header value for a single-valued header (used by the cache middleware).</summary>
internal static class ElsieHeadersCacheExtensions
{
    public static string? GetSingle(this ElsieHeaders headers, string name)
    {
        if (headers.TryGetValues(name, out var values) && values.Count > 0)
        {
            return values[0];
        }

        return null;
    }
}
