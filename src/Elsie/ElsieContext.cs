using System.Globalization;
using System.Text.Json;
using Elsie.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie;

/// <summary>
/// Per-request facade available to Elsie route handlers (host-agnostic).
/// </summary>
public sealed class ElsieContext
{
    private readonly RouteTable? _routes;

    public ElsieContext(
        ElsieRequest request,
        ElsieResponse response,
        IReadOnlyDictionary<string, string> routeValues,
        JsonSerializerOptions? jsonSerializerOptions = null,
        RouteTable? routes = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Response = response ?? throw new ArgumentNullException(nameof(response));
        RouteValues = routeValues ?? throw new ArgumentNullException(nameof(routeValues));
        JsonSerializerOptions = jsonSerializerOptions ?? ElsieJson.DefaultOptions;
        _routes = routes;
    }

    public ElsieRequest Request { get; }
    public ElsieResponse Response { get; }
    public IReadOnlyDictionary<string, string> RouteValues { get; }
    public IServiceProvider RequestServices => Request.RequestServices;

    /// <summary>Alias for <see cref="RequestServices"/>.</summary>
    public IServiceProvider Services => Request.RequestServices;

    public CancellationToken RequestAborted => Request.RequestAborted;

    /// <summary>JSON options for this request (from <see cref="ElsieOptions"/>).</summary>
    public JsonSerializerOptions JsonSerializerOptions { get; }

    /// <summary>Resolve a required service from the current request scope.</summary>
    public T GetRequiredService<T>() where T : notnull =>
        RequestServices.GetRequiredService<T>();

    /// <summary>Resolve an optional service from the current request scope.</summary>
    public T? GetService<T>() => RequestServices.GetService<T>();

    public string? RouteOrDefault(string key) =>
        RouteValues.TryGetValue(key, out var value) ? value : null;

    public string? QueryOrDefault(string key) => Request.GetQuery(key);

    public bool TryGetRouteInt(string key, out int value)
    {
        value = default;
        return RouteValues.TryGetValue(key, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetRouteLong(string key, out long value)
    {
        value = default;
        return RouteValues.TryGetValue(key, out var raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetRouteGuid(string key, out Guid value)
    {
        value = default;
        return RouteValues.TryGetValue(key, out var raw) && Guid.TryParse(raw, out value);
    }

    public bool TryGetRouteBool(string key, out bool value)
    {
        value = default;
        return RouteValues.TryGetValue(key, out var raw) && bool.TryParse(raw, out value);
    }

    public bool TryGetQueryInt(string key, out int value)
    {
        value = default;
        var raw = QueryOrDefault(key);
        return raw is not null
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetQueryBool(string key, out bool value)
    {
        value = default;
        var raw = QueryOrDefault(key);
        return raw is not null && bool.TryParse(raw, out value);
    }

    /// <summary>
    /// Required route int. On failure sets <paramref name="error"/> to 400 problem result.
    /// </summary>
    public bool RequireRouteInt(string key, out int value, out ElsieResult? error)
    {
        if (TryGetRouteInt(key, out value))
        {
            error = null;
            return true;
        }

        error = ElsieResult.BadRequest($"Route value '{key}' must be an integer.");
        return false;
    }

    /// <summary>
    /// Required route GUID. On failure sets <paramref name="error"/> to 400 problem result.
    /// </summary>
    public bool RequireRouteGuid(string key, out Guid value, out ElsieResult? error)
    {
        if (TryGetRouteGuid(key, out value))
        {
            error = null;
            return true;
        }

        error = ElsieResult.BadRequest($"Route value '{key}' must be a GUID.");
        return false;
    }

    /// <summary>
    /// Build a path for a named route. Values may be a dictionary or an anonymous object.
    /// </summary>
    public string UrlFor(string name, object? values = null)
    {
        if (_routes is null)
        {
            throw new InvalidOperationException(
                "Link generation requires a RouteTable on the context (normal dispatcher path).");
        }

        return _routes.GetPathByName(name, values);
    }

    public async Task<T?> ReadJsonAsync<T>(CancellationToken cancellationToken = default)
    {
        return await JsonSerializer.DeserializeAsync<T>(
            Request.Body,
            JsonSerializerOptions,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserialize JSON body. Returns a failed bind result (400 problem+json) when missing/invalid.
    /// </summary>
    public async Task<ElsieBindResult<T>> BindJsonAsync<T>(CancellationToken cancellationToken = default)
    {
        try
        {
            if (Request.ContentLength is 0)
            {
                return ElsieBindResult<T>.Fail(ElsieResult.BadRequest("JSON body is required."));
            }

            var value = await JsonSerializer.DeserializeAsync<T>(
                Request.Body,
                JsonSerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (value is null)
            {
                return ElsieBindResult<T>.Fail(ElsieResult.BadRequest("JSON body is required."));
            }

            return ElsieBindResult<T>.Success(value);
        }
        catch (JsonException ex)
        {
            return ElsieBindResult<T>.Fail(ElsieResult.BadRequest($"Invalid JSON: {ex.Message}"));
        }
    }

    /// <summary>Serialize <paramref name="value"/> with this request's JSON options (app options).</summary>
    public ElsieResult Json<T>(T value, int statusCode = 200) =>
        ElsieResult.Json(value, statusCode, JsonSerializerOptions);
}
