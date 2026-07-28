namespace Elsie;

/// <summary>
/// Fully baked HTTP response for any host to write.
/// Produced by <see cref="FromDispatch"/> — single materialization path for ASP.NET + in-memory.
/// </summary>
public sealed class ElsieHttpResponse
{
    private ElsieHttpResponse(
        int statusCode,
        string? contentType,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte>? body,
        Func<Stream, CancellationToken, Task>? bodyWriter)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Headers = headers;
        Body = body;
        BodyWriter = bodyWriter;
    }

    public int StatusCode { get; }
    public string? ContentType { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public ReadOnlyMemory<byte>? Body { get; }
    public Func<Stream, CancellationToken, Task>? BodyWriter { get; }

    /// <summary>
    /// Materialize a dispatch outcome.
    /// Returns <c>null</c> for <see cref="ElsieDispatchStatus.NotFound"/> (host fallthrough).
    /// Header merge: <see cref="ElsieResponse"/> (hooks) then <see cref="ElsieResult"/> headers.
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
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Allow"] = string.Join(", ", outcome.AllowedMethods)
                };
                return new(405, contentType: null, headers, body: null, bodyWriter: null);
            }

            case ElsieDispatchStatus.Handled:
            {
                var result = outcome.Result!;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (outcome.Response is not null)
                {
                    foreach (var h in outcome.Response.Headers)
                    {
                        headers[h.Key] = h.Value;
                    }
                }

                foreach (var h in result.Headers)
                {
                    headers[h.Key] = h.Value;
                }

                return new(result.StatusCode, result.ContentType, headers, result.Body, result.BodyWriter);
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
