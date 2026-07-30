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

    public static ElsieResult NotModified() => Status(304);

    public static ElsieResult BadRequest(string? detail = null) =>
        Problem(400, title: "Bad Request", detail);

    public static ElsieResult Unauthorized(string? detail = null) =>
        Problem(401, title: "Unauthorized", detail);

    public static ElsieResult Forbidden(string? detail = null) =>
        Problem(403, title: "Forbidden", detail);

    public static ElsieResult NotFound(string? detail = null) =>
        Problem(404, title: "Not Found", detail);

    public static ElsieResult NotAcceptable(string? detail = null) =>
        Problem(406, title: "Not Acceptable", detail);

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

    public static ElsieResult Html(string html, int statusCode = 200) =>
        Text(html, statusCode, "text/html; charset=utf-8");

    public static ElsieResult Bytes(ReadOnlyMemory<byte> bytes, string contentType, int statusCode = 200) =>
        new(statusCode, contentType, bytes, bodyWriter: null, headers: null);

    /// <summary>Buffered file payload with optional Content-Disposition attachment name.</summary>
    public static ElsieResult File(ReadOnlyMemory<byte> bytes, string contentType, string? downloadName = null, int statusCode = 200)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var result = new ElsieResult(statusCode, contentType, bytes, bodyWriter: null, headers: null);
        return downloadName is null ? result : result.WithHeader("Content-Disposition", ContentDisposition(downloadName));
    }

    /// <summary>Streamed file payload. Stream is disposed after write unless <paramref name="leaveOpen"/>.</summary>
    public static ElsieResult File(Stream stream, string contentType, string? downloadName = null, bool leaveOpen = false, int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var result = new ElsieResult(
            statusCode,
            contentType,
            body: null,
            bodyWriter: async (output, ct) =>
            {
                try
                {
                    await stream.CopyToAsync(output, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (!leaveOpen)
                    {
                        await stream.DisposeAsync().ConfigureAwait(false);
                    }
                }
            },
            headers: null);
        return downloadName is null ? result : result.WithHeader("Content-Disposition", ContentDisposition(downloadName));
    }

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

    /// <summary>Writer-based <c>text/event-stream</c> response (works with in-memory hosts).</summary>
    public static ElsieResult ServerSentEvents(Func<ElsieSseWriter, CancellationToken, Task> writer, int statusCode = 200)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new(
            statusCode,
            "text/event-stream",
            body: null,
            bodyWriter: async (stream, ct) =>
            {
                var sse = new ElsieSseWriter(stream);
                await writer(sse, ct).ConfigureAwait(false);
            },
            headers: null);
    }

    /// <summary>Creates a redirect response (302 by default, 301 when permanent).</summary>
    public static ElsieResult Redirect(string location, bool permanent = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        return RedirectCore(permanent ? 301 : 302, location);
    }

    public static ElsieResult RedirectTemporary(string location) => RedirectCore(307, location);

    public static ElsieResult RedirectPermanent(string location) => RedirectCore(308, location);

    public static ElsieResult Created(string? location = null, object? body = null, JsonSerializerOptions? options = null)
    {
        ElsieResult result;
        if (body is null)
        {
            result = Status(201);
        }
        else
        {
            result = Json(body, statusCode: 201, options);
        }

        return location is null ? result : result.WithHeader("Location", location);
    }

    public static ElsieResult Accepted(string? location = null, object? body = null, JsonSerializerOptions? options = null)
    {
        ElsieResult result;
        if (body is null)
        {
            result = Status(202);
        }
        else
        {
            result = Json(body, statusCode: 202, options);
        }

        return location is null ? result : result.WithHeader("Location", location);
    }

    /// <summary>
    /// 304 when <paramref name="ifNoneMatch"/> contains <paramref name="etag"/> (weak/strong tolerant strip of W/);
    /// otherwise returns <paramref name="whenModified"/> with ETag set.
    /// </summary>
    public static ElsieResult IfNoneMatch(string? ifNoneMatch, string etag, ElsieResult whenModified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        ArgumentNullException.ThrowIfNull(whenModified);

        if (EtagMatches(ifNoneMatch, etag))
        {
            return NotModified().WithHeader("ETag", etag);
        }

        return whenModified.WithHeader("ETag", etag);
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

    /// <summary>Returns a copy with multiple headers set (Set semantics per key).</summary>
    public ElsieResult WithHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var bag = _headers?.Clone() ?? new ElsieHeaders();
        foreach (var (name, value) in headers)
        {
            bag.Set(name, value);
        }

        return new(StatusCode, ContentType, Body, BodyWriter, bag);
    }

    public ElsieResult WithCookie(string name, string value, ElsieCookieOptions? options = null)
    {
        var headers = _headers?.Clone() ?? new ElsieHeaders();
        headers.Add("Set-Cookie", ElsieCookieFormatter.FormatSetCookie(name, value, options));
        return new(StatusCode, ContentType, Body, BodyWriter, headers);
    }

    private static ElsieResult RedirectCore(int status, string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var headers = new ElsieHeaders();
        headers.Set("Location", location);
        return new(status, contentType: null, body: null, bodyWriter: null, headers);
    }

    private static string ContentDisposition(string downloadName)
    {
        // ASCII filename + RFC 5987 filename*
        var escaped = downloadName.Replace("\"", "'", StringComparison.Ordinal);
        return $"attachment; filename=\"{escaped}\"; filename*=UTF-8''{Uri.EscapeDataString(downloadName)}";
    }

    internal static bool EtagMatches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        if (ifNoneMatch.Trim() == "*")
        {
            return true;
        }

        var target = NormalizeEtag(etag);
        foreach (var part in ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(NormalizeEtag(part), target, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeEtag(string value)
    {
        value = value.Trim();
        if (value.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..].Trim();
        }

        return value;
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
