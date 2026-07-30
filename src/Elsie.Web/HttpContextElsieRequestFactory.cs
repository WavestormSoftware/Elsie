using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Elsie.Web;

internal static class HttpContextElsieRequestFactory
{
    public static ElsieRequest Create(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var request = httpContext.Request;

        var elsieRequest = new ElsieRequest(
            method: request.Method,
            path: request.Path.Value ?? "/",
            body: request.Body,
            contentLength: request.ContentLength,
            contentType: request.ContentType,
            requestServices: httpContext.RequestServices,
            requestAborted: httpContext.RequestAborted,
            queryValues: StringValuesMap.FromQuery(request.Query),
            headerValues: StringValuesMap.FromHeaders(request.Headers),
            scheme: request.Scheme,
            host: request.Host.Value,
            pathBase: request.PathBase.Value,
            protocol: request.Protocol,
            remoteIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            queryString: request.QueryString.HasValue ? request.QueryString.Value : string.Empty);
        elsieRequest.SetHttpContext(httpContext);
        return elsieRequest;
    }

    private sealed class StringValuesMap : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        private readonly int _count;
        private readonly IEnumerable<string> _keys;
        private readonly Func<string, bool> _containsKey;
        private readonly TryGetValueHandler _tryGetValue;
        private readonly IEnumerable<KeyValuePair<string, StringValues>> _pairs;

        private delegate bool TryGetValueHandler(string key, out StringValues values);

        private StringValuesMap(
            int count,
            IEnumerable<string> keys,
            Func<string, bool> containsKey,
            TryGetValueHandler tryGetValue,
            IEnumerable<KeyValuePair<string, StringValues>> pairs)
        {
            _count = count;
            _keys = keys;
            _containsKey = containsKey;
            _tryGetValue = tryGetValue;
            _pairs = pairs;
        }

        public static StringValuesMap FromQuery(IQueryCollection query) =>
            new(query.Count, query.Keys, query.ContainsKey, query.TryGetValue, query);

        public static StringValuesMap FromHeaders(IHeaderDictionary headers) =>
            new(headers.Count, headers.Keys, headers.ContainsKey, headers.TryGetValue, headers);

        public IReadOnlyList<string> this[string key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => _keys;

        public IEnumerable<IReadOnlyList<string>> Values
        {
            get
            {
                foreach (var key in _keys)
                {
                    yield return Wrap(GetRequired(key));
                }
            }
        }

        public int Count => _count;

        public bool ContainsKey(string key) => _containsKey(key);

        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
        {
            foreach (var kv in _pairs)
            {
                yield return new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, Wrap(kv.Value));
            }
        }

        public bool TryGetValue(string key, out IReadOnlyList<string> value)
        {
            if (_tryGetValue(key, out var values))
            {
                value = Wrap(values);
                return true;
            }

            value = null!;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private StringValues GetRequired(string key) =>
            _tryGetValue(key, out var values) ? values : default;

        private static IReadOnlyList<string> Wrap(StringValues values)
        {
            if (values.Count == 0)
            {
                return Array.Empty<string>();
            }

            if (values.Count == 1)
            {
                return new[] { values[0] ?? string.Empty };
            }

            var list = new string[values.Count];
            var dirty = false;
            for (var i = 0; i < values.Count; i++)
            {
                var v = values[i];
                if (v is null)
                {
                    dirty = true;
                    list[i] = string.Empty;
                }
                else
                {
                    list[i] = v;
                }
            }

            return dirty ? list : (IReadOnlyList<string>)values;
        }
    }
}
