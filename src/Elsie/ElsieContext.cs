using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie;

/// <summary>
/// Per-request facade available to Elsie route handlers.
/// </summary>
public sealed class ElsieContext
{
    public ElsieContext(
        HttpContext httpContext,
        IReadOnlyDictionary<string, string> routeValues,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        HttpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
        RouteValues = routeValues ?? throw new ArgumentNullException(nameof(routeValues));
        JsonSerializerOptions = jsonSerializerOptions ?? ElsieJson.DefaultOptions;
    }

    public HttpContext HttpContext { get; }
    public HttpRequest Request => HttpContext.Request;
    public HttpResponse Response => HttpContext.Response;
    public IReadOnlyDictionary<string, string> RouteValues { get; }
    public IServiceProvider RequestServices => HttpContext.RequestServices;
    public CancellationToken RequestAborted => HttpContext.RequestAborted;
    public IQueryCollection Query => Request.Query;

    /// <summary>JSON options for this request (from <see cref="ElsieOptions"/>).</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>Resolve a required service from the current request scope.</summary>
    public T GetRequiredService<T>() where T : notnull =>
        RequestServices.GetRequiredService<T>();

    /// <summary>Resolve an optional service from the current request scope.</summary>
    public T? GetService<T>() => RequestServices.GetService<T>();

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
            JsonSerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Serialize <paramref name="value"/> with this request's JSON options.</summary>
    public ElsieResult Json<T>(T value, int statusCode = 200) =>
        ElsieResult.Json(value, statusCode, JsonSerializerOptions);
}
