using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Elsie;

/// <summary>
/// Per-request facade available to Elsie route handlers.
/// </summary>
public sealed class ElsieContext
{
    public ElsieContext(HttpContext httpContext, IReadOnlyDictionary<string, string> routeValues)
    {
        HttpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
        RouteValues = routeValues ?? throw new ArgumentNullException(nameof(routeValues));
    }

    public HttpContext HttpContext { get; }
    public HttpRequest Request => HttpContext.Request;
    public HttpResponse Response => HttpContext.Response;
    public IReadOnlyDictionary<string, string> RouteValues { get; }
    public IServiceProvider RequestServices => HttpContext.RequestServices;
    public CancellationToken RequestAborted => HttpContext.RequestAborted;

    public async Task<T?> ReadJsonAsync<T>(CancellationToken cancellationToken = default)
    {
        return await JsonSerializer.DeserializeAsync<T>(
            Request.Body,
            ElsieJson.DefaultOptions,
            cancellationToken).ConfigureAwait(false);
    }
}
