using Microsoft.AspNetCore.Http;

namespace Elsie.AspNetCore;

public sealed class ElsieResultExecutor : IElsieResultExecutor
{
    public async Task ExecuteAsync(HttpContext httpContext, ElsieResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(result);

        httpContext.Response.StatusCode = result.StatusCode;

        foreach (var header in result.Headers)
        {
            httpContext.Response.Headers[header.Key] = header.Value;
        }

        if (!string.IsNullOrEmpty(result.ContentType))
        {
            httpContext.Response.ContentType = result.ContentType;
        }

        if (result.BodyWriter is not null)
        {
            await result.BodyWriter(httpContext.Response.Body, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (result.Body is { } body && body.Length > 0)
        {
            await httpContext.Response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        }
    }
}
