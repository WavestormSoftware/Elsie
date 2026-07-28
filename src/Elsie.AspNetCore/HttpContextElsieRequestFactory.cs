using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Elsie.AspNetCore;

internal static class HttpContextElsieRequestFactory
{
    public static ElsieRequest Create(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var request = httpContext.Request;

        var (query, queryValues) = CopyMulti(request.Query);
        var (headers, headerValues) = CopyMulti(request.Headers);

        var elsieRequest = new ElsieRequest(
            method: request.Method,
            path: request.Path.Value ?? "/",
            query: query,
            headers: headers,
            body: request.Body,
            contentLength: request.ContentLength,
            contentType: request.ContentType,
            requestServices: httpContext.RequestServices,
            requestAborted: httpContext.RequestAborted,
            queryValues: queryValues,
            headerValues: headerValues);
        elsieRequest.SetHttpContext(httpContext);
        return elsieRequest;
    }

    private static (
        Dictionary<string, string> First,
        Dictionary<string, IReadOnlyList<string>> All)
        CopyMulti(IEnumerable<KeyValuePair<string, StringValues>> source)
    {
        var first = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var all = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
        {
            var values = kv.Value;
            var list = new string[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                list[i] = values[i] ?? string.Empty;
            }

            all[kv.Key] = list;
            first[kv.Key] = list.Length > 0 ? list[0] : string.Empty;
        }

        return (first, all);
    }
}
