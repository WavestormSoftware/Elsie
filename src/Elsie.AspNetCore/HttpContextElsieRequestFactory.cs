using Microsoft.AspNetCore.Http;

namespace Elsie.AspNetCore;

internal static class HttpContextElsieRequestFactory
{
    public static ElsieRequest Create(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var request = httpContext.Request;

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in request.Query)
        {
            query[kv.Key] = kv.Value.ToString();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in request.Headers)
        {
            headers[kv.Key] = kv.Value.ToString();
        }

        var elsieRequest = new ElsieRequest(
            method: request.Method,
            path: request.Path.Value ?? "/",
            query: query,
            headers: headers,
            body: request.Body,
            contentLength: request.ContentLength,
            contentType: request.ContentType,
            requestServices: httpContext.RequestServices,
            requestAborted: httpContext.RequestAborted);
        elsieRequest.SetHttpContext(httpContext);
        return elsieRequest;
    }
}
