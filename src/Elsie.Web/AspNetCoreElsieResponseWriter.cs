using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Elsie.Web;

/// <summary>
/// Single write path from <see cref="ElsieHttpResponse"/> onto ASP.NET <see cref="HttpResponse"/>.
/// Multi-value headers/Set-Cookie, Content-Length when buffered, HEAD body suppression.
/// </summary>
internal static class AspNetCoreElsieResponseWriter
{
    public static async Task WriteAsync(
        HttpContext httpContext,
        ElsieHttpResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(response);

        var httpResponse = httpContext.Response;
        httpResponse.StatusCode = response.StatusCode;

        foreach (var (name, values) in response.Headers)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            httpResponse.Headers[name] = values.Count == 1
                ? new StringValues(values[0])
                : new StringValues(values as string[] ?? values.ToArray());
        }

        if (!string.IsNullOrEmpty(response.ContentType))
        {
            httpResponse.ContentType = response.ContentType;
        }

        var isHead = HttpMethods.IsHead(httpContext.Request.Method);

        if (response.Body is { } body)
        {
            httpResponse.ContentLength = body.Length;
            if (!isHead && body.Length > 0)
            {
                await httpResponse.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (response.BodyWriter is not null && !isHead)
        {
            await response.BodyWriter(httpResponse.Body, cancellationToken).ConfigureAwait(false);
        }
    }
}
