using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Elsie.AspNetCore;

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
            queryValues: CopyMulti(request.Query),
            headerValues: CopyMulti(request.Headers),
            scheme: request.Scheme,
            host: request.Host.Value,
            pathBase: request.PathBase.Value,
            protocol: request.Protocol,
            remoteIp: httpContext.Connection.RemoteIpAddress?.ToString(),
            queryString: request.QueryString.HasValue ? request.QueryString.Value : string.Empty);
        elsieRequest.SetHttpContext(httpContext);
        return elsieRequest;
    }

    private static Dictionary<string, IReadOnlyList<string>> CopyMulti(
        IEnumerable<KeyValuePair<string, StringValues>> source)
    {
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
        }

        return all;
    }
}
