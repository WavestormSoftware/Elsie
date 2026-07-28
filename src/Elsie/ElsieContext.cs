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
    public IQueryCollection Query => Request.Query;

    public string? QueryOrDefault(string key) =>
        Request.Query.TryGetValue(key, out var values) ? values.ToString() : null;

    public bool TryGetRouteInt(string key, out int value)
    {
        value = default;
        return RouteValues.TryGetValue(key, out var raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public async Task<T?> ReadJsonAsync<T>(CancellationToken cancellationToken = default)
    {
        return await JsonSerializer.DeserializeAsync<T>(
            Request.Body,
            ElsieJson.DefaultOptions,
            cancellationToken).ConfigureAwait(false);
    }
}
