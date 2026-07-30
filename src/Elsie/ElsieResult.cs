using System.Text;
using System.Text.Json;

namespace Elsie;

/// <summary>
/// Describes an HTTP response produced by an Elsie route handler.
/// </summary>
public sealed class ElsieResult
{
    private readonly ElsieHeaders? _headers;

    private ElsieResult(
        int statusCode,
        string? contentType,
        ReadOnlyMemory<byte>? body,
        Func<Stream, CancellationToken, Task>? bodyWriter,
        ElsieHeaders? headers)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
        BodyWriter = bodyWriter;
        _headers = headers;
    }

    public int StatusCode { get; }
    public string? ContentType { get; }
    public ReadOnlyMemory<byte>? Body { get; }
    public Func<Stream, CancellationToken, Task>? BodyWriter { get; }

    public ElsieHeaders Headers => _headers ?? EmptyHeadersHolder.Instance;

    public static ElsieResult Status(int statusCode) =>
        new(statusCode, contentType: null, body: null, bodyWriter: null, headers: null);

    public static ElsieResult NoContent() => Status(204);

    public static ElsieResult BadRequest(string? detail = null) =>
        Problem(400, title: "Bad Request", detail);

    public static ElsieResult Unauthorized(string? detail = null) =>
        Problem(401, title: "Unauthorized", detail);

    public static ElsieResult Forbidden(string? detail = null) =>
        Problem(403, title: "Forbidden", detail);

    public static ElsieResult NotFound(string? detail = null) =>
        Problem(404, title: "Not Found", detail);

    public static ElsieResult Conflict(string? detail = null) =>
        Problem(409, title: "Conflict", detail);

    /// <summary>
    /// Lightweight RFC 7807-style JSON problem body (no external dependency).
    /// </summary>
    public static ElsieResult Problem(int statusCode, string title, string? detail = null, JsonSerializerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = statusCode,
            ["title"] = title
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            payload["detail"] = detail;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, options ?? ElsieJson.DefaultOptions);
        return new(statusCode, "application/problem+json; charset=utf-8", bytes, bodyWriter: null, headers: null);
    }

    /// <summary>
    /// 400 problem+json with an <c>errors</c> object (property → messages), ASP.NET-style.
    /// </summary>
    public static ElsieResult ValidationProblem(
        IReadOnlyDictionary<string, string[]> errors,
        string? detail = null,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = 400,
            ["title"] = "Validation Failed",
            ["errors"] = errors
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            payload["detail"] = detail;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, options ?? ElsieJson.DefaultOptions);
        return new(400, "application/problem+json; charset=utf-8", bytes, bodyWriter: null, headers: null);
    }

    public static ElsieResult Text(string text, int statusCode = 200, string contentType = "text/plain; charset=utf-8")
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(statusCode, contentType, Encoding.UTF8.GetBytes(text), bodyWriter: null, headers: null);
    }

    public static ElsieResult Bytes(ReadOnlyMemory<byte> bytes, string contentType, int statusCode = 200) =>
        new(statusCode, contentType, bytes, bodyWriter: null, headers: null);

    /// <summary>
    /// Serialize with framework defaults (<see cref="ElsieJson.DefaultOptions"/>) unless
    /// <paramref name="options"/> is provided. Prefer <see cref="ElsieContext.Json{T}"/> for app options.
    /// </summary>
    public static ElsieResult Json<T>(T value, int statusCode = 200, JsonSerializerOptions? options = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, options ?? ElsieJson.DefaultOptions);
        return new(statusCode, "application/json; charset=utf-8", payload, bodyWriter: null, headers: null);
    }

    public static ElsieResult Stream(Func<Stream, CancellationToken, Task> writer, string contentType, int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new(statusCode, contentType, body: null, bodyWriter: writer, headers: null);
    }

    /// <summary>Creates a redirect response (302 by default, 301 when permanent).</summary>
    public static ElsieResult Redirect(string location, bool permanent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var headers = new ElsieHeaders();
        headers.Set("Location", location);
        return new(permanent ? 301 : 302, contentType: null, body: null, bodyWriter: null, headers);
    }

    /// <summary>Returns a copy of this result with an additional response header (Set semantics).</summary>
    public ElsieResult WithHeader(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var headers = _headers?.Clone() ?? new ElsieHeaders();
        headers.Set(name, value);
        return new(StatusCode, ContentType, Body, BodyWriter, headers);
    }

    private static class EmptyHeadersHolder
    {
        public static readonly ElsieHeaders Instance = new();
    }
}

/// <summary>
/// Shared framework JSON defaults for Elsie.
/// Immutable fallback — do not mutate. App configuration goes on <see cref="ElsieOptions.JsonSerializerOptions"/>
/// and is used by <see cref="ElsieContext.Json{T}"/> / binding. Static <see cref="ElsieResult.Json{T}"/> uses these defaults.
/// </summary>
public static class ElsieJson
{
    private static readonly JsonSerializerOptions s_defaultOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Framework default JSON options. Treat as immutable.</summary>
    public static JsonSerializerOptions DefaultOptions => s_defaultOptions;
}
