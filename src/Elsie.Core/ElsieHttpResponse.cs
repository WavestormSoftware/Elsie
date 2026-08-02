namespace Elsie;

/// <summary>
/// Fully baked HTTP response for any host to write.
/// Produced by <see cref="FromDispatch"/> — single materialization path for host + in-memory.
/// </summary>
public sealed class ElsieHttpResponse
{
    private ElsieHttpResponse(
        int statusCode,
        string? contentType,
        ElsieHeaders headers,
        ReadOnlyMemory<byte>? body,
        Func<Stream, CancellationToken, Task>? bodyWriter,
        Func<ElsieWebSocket, CancellationToken, Task>? webSocketHandler = null,
        IReadOnlyList<KeyValuePair<string, string>>? trailers = null)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Headers = headers;
        Body = body;
        BodyWriter = bodyWriter;
        WebSocketHandler = webSocketHandler;
        Trailers = trailers ?? Array.Empty<KeyValuePair<string, string>>();
    }

    public int StatusCode { get; }
    public string? ContentType { get; }
    public ElsieHeaders Headers { get; }
    public ReadOnlyMemory<byte>? Body { get; }
    public Func<Stream, CancellationToken, Task>? BodyWriter { get; }
    public Func<ElsieWebSocket, CancellationToken, Task>? WebSocketHandler { get; }

    /// <summary>Response trailers (HTTP/2 / HTTP/3 trailing HEADERS after the body).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Trailers { get; }

    /// <summary>Construct a response directly (host transforms, compression, static files).</summary>
    public static ElsieHttpResponse Create(
        int statusCode,
        string? contentType,
        ElsieHeaders headers,
        ReadOnlyMemory<byte>? body,
        Func<Stream, CancellationToken, Task>? bodyWriter = null,
        Func<ElsieWebSocket, CancellationToken, Task>? webSocketHandler = null,
        IReadOnlyList<KeyValuePair<string, string>>? trailers = null) =>
        new(statusCode, contentType, headers ?? new ElsieHeaders(), body, bodyWriter, webSocketHandler, trailers);

    /// <summary>Construct a response directly (host transforms, compression, static files).</summary>
    public static ElsieHttpResponse Create(
        int statusCode,
        string? contentType,
        ElsieHeaders headers,
        ReadOnlyMemory<byte>? body,
        Func<Stream, CancellationToken, Task>? bodyWriter = null,
        Func<ElsieWebSocket, CancellationToken, Task>? webSocketHandler = null) =>
        new(statusCode, contentType, headers ?? new ElsieHeaders(), body, bodyWriter, webSocketHandler);

    /// <summary>
    /// Materialize a dispatch outcome.
    /// Returns <c>null</c> for <see cref="ElsieDispatchStatus.NotFound"/> (host fallthrough).
    /// Header merge: hook headers → result headers → Set-Cookie from <see cref="ElsieResponse"/>.
    /// </summary>
    public static ElsieHttpResponse? FromDispatch(ElsieDispatchResult outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome.Status)
        {
            case ElsieDispatchStatus.NotFound:
                return null;

            case ElsieDispatchStatus.MethodNotAllowed:
                {
                    var headers = new ElsieHeaders();
                    headers.Set("Allow", string.Join(", ", outcome.AllowedMethods));
                    var problem = ElsieResult.Problem(
                        405,
                        title: "Method Not Allowed",
                        detail: $"Allowed: {string.Join(", ", outcome.AllowedMethods)}");
                    return new(405, problem.ContentType, headers, problem.Body, bodyWriter: null);
                }

            case ElsieDispatchStatus.Handled:
                {
                    var result = outcome.Result!;
                    var headers = new ElsieHeaders();
                    if (outcome.Response is not null)
                    {
                        headers.MergeFrom(outcome.Response.Headers);
                    }

                    headers.MergeFrom(result.Headers);

                    if (outcome.Response is not null)
                    {
                        foreach (var cookie in outcome.Response.SetCookies)
                        {
                            headers.Add("Set-Cookie", cookie);
                        }
                    }

                    return new(
                        result.StatusCode,
                        result.ContentType,
                        headers,
                        result.Body,
                        result.BodyWriter,
                        result.WebSocketHandler,
                        outcome.Response?.Trailers);
                }

            default:
                throw new InvalidOperationException($"Unknown dispatch status '{outcome.Status}'.");
        }
    }

    /// <summary>Write body to <paramref name="stream"/> (BodyWriter preferred, else Body bytes).</summary>
    public async Task WriteBodyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (BodyWriter is not null)
        {
            await BodyWriter(stream, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Body is { Length: > 0 } memory)
        {
            await stream.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Buffer body into a byte array (tests / in-memory hosts).</summary>
    public async Task<byte[]> BufferBodyAsync(CancellationToken cancellationToken = default)
    {
        if (BodyWriter is not null)
        {
            await using var ms = new MemoryStream();
            await BodyWriter(ms, cancellationToken).ConfigureAwait(false);
            return ms.ToArray();
        }

        if (Body is { } memory)
        {
            return memory.ToArray();
        }

        return Array.Empty<byte>();
    }
}
