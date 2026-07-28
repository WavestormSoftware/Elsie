using System.Text;
using System.Text.Json;

namespace Elsie;

/// <summary>
/// Describes an HTTP response produced by an Elsie route handler.
/// </summary>
public sealed class ElsieResult
{
    private ElsieResult(
        int statusCode,
        string? contentType,
        ReadOnlyMemory<byte>? body,
        Func<Stream, CancellationToken, Task>? bodyWriter)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
        BodyWriter = bodyWriter;
    }

    public int StatusCode { get; }
    public string? ContentType { get; }
    public ReadOnlyMemory<byte>? Body { get; }
    public Func<Stream, CancellationToken, Task>? BodyWriter { get; }

    public static ElsieResult Status(int statusCode) =>
        new(statusCode, contentType: null, body: null, bodyWriter: null);

    public static ElsieResult NoContent() => Status(StatusCodes.Status204NoContent);

    public static ElsieResult Text(string text, int statusCode = StatusCodes.Status200OK, string contentType = "text/plain; charset=utf-8")
    {
        ArgumentNullException.ThrowIfNull(text);
        return new ElsieResult(statusCode, contentType, Encoding.UTF8.GetBytes(text), bodyWriter: null);
    }

    public static ElsieResult Bytes(ReadOnlyMemory<byte> bytes, string contentType, int statusCode = StatusCodes.Status200OK) =>
        new(statusCode, contentType, bytes, bodyWriter: null);

    public static ElsieResult Json<T>(T value, int statusCode = StatusCodes.Status200OK, JsonSerializerOptions? options = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, options ?? ElsieJson.DefaultOptions);
        return new ElsieResult(statusCode, "application/json; charset=utf-8", payload, bodyWriter: null);
    }

    public static ElsieResult Stream(Func<Stream, CancellationToken, Task> writer, string contentType, int statusCode = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new ElsieResult(statusCode, contentType, body: null, bodyWriter: writer);
    }
}

/// <summary>Shared JSON defaults for Elsie.</summary>
public static class ElsieJson
{
    public static JsonSerializerOptions DefaultOptions { get; } = new(JsonSerializerDefaults.Web);
}

// Avoid hard dependency name clash when FrameworkReference provides StatusCodes
file static class StatusCodes
{
    public const int Status200OK = 200;
    public const int Status204NoContent = 204;
}
