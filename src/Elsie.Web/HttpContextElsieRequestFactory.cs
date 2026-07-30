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
            queryValues: new QueryCollectionMap(request.Query),
            headerValues: new HeaderDictionaryMap(request.Headers),
            scheme: request.Scheme,
            host: request.Host.Value,
            pathBase: request.PathBase.Value,
            protocol: request.Protocol,
            remoteIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            queryString: request.QueryString.HasValue ? request.QueryString.Value : string.Empty);
        elsieRequest.SetHttpContext(httpContext);
        return elsieRequest;
    }

    private sealed class QueryCollectionMap : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        private readonly IQueryCollection _query;

        public QueryCollectionMap(IQueryCollection query) => _query = query;

        public IReadOnlyList<string> this[string key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => _query.Keys;
        public IEnumerable<IReadOnlyList<string>> Values
        {
            get
            {
                foreach (var key in _query.Keys)
                {
                    yield return Wrap(_query[key]);
                }
            }
        }

        public int Count => _query.Count;

        public bool ContainsKey(string key) => _query.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
        {
            foreach (var kv in _query)
            {
                yield return new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, Wrap(kv.Value));
            }
        }

        public bool TryGetValue(string key, out IReadOnlyList<string> value)
        {
            if (_query.TryGetValue(key, out var values))
            {
                value = Wrap(values);
                return true;
            }

            value = null!;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class HeaderDictionaryMap : IReadOnlyDictionary<string, IReadOnlyList<string>>
    {
        private readonly IHeaderDictionary _headers;

        public HeaderDictionaryMap(IHeaderDictionary headers) => _headers = headers;

        public IReadOnlyList<string> this[string key] =>
            TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => _headers.Keys;
        public IEnumerable<IReadOnlyList<string>> Values
        {
            get
            {
                foreach (var key in _headers.Keys)
                {
                    yield return Wrap(_headers[key]);
                }
            }
        }

        public int Count => _headers.Count;

        public bool ContainsKey(string key) => _headers.ContainsKey(key);

        public IEnumerator<KeyValuePair<string, IReadOnlyList<string>>> GetEnumerator()
        {
            foreach (var kv in _headers)
            {
                yield return new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, Wrap(kv.Value));
            }
        }

        public bool TryGetValue(string key, out IReadOnlyList<string> value)
        {
            if (_headers.TryGetValue(key, out var values))
            {
                value = Wrap(values);
                return true;
            }

            value = null!;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static IReadOnlyList<string> Wrap(StringValues values)
    {
        // StringValues implements IList<string> / IReadOnlyList on modern TFMs; materialize only if nulls appear.
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
