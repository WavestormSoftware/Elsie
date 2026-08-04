using System.Globalization;
using System.IO.Compression;

namespace Elsie.Web.Hosting;

internal static class ResponseCompression
{
    public static ElsieHttpResponse MaybeCompress(
        ElsieRequest request,
        ElsieHttpResponse response,
        int minBodyBytes)
    {
        if (response.WebSocketHandler is not null)
        {
            return response;
        }

        // Streaming (BodyWriter) responses: wrap the outgoing stream with a compression
        // stream so each written chunk is compressed incrementally. Length is usually
        // unknown, so compress whenever the client negotiates an encoding.
        if (response.BodyWriter is not null)
        {
            return MaybeCompressStreaming(request, response, minBodyBytes);
        }

        if (response.Body is not { Length: var len } body || len < minBodyBytes)
        {
            return EnsureVary(response, compressibleCandidate: false);
        }

        if (response.Headers.Contains("Content-Encoding"))
        {
            return EnsureVary(response, compressibleCandidate: true);
        }

        var ct = response.ContentType ?? string.Empty;
        if (!IsCompressible(ct))
        {
            return response;
        }

        var encoding = ChooseEncoding(request.GetHeader("Accept-Encoding"));
        if (encoding is null)
        {
            // Compressible response left uncompressed — still advertise Vary for caches.
            return EnsureVary(response, compressibleCandidate: true);
        }

        var compressed = encoding switch
        {
            "br" => CompressBrotli(body.Span),
            "gzip" => CompressGzip(body.Span),
            _ => null
        };

        if (compressed is null || compressed.Length >= body.Length)
        {
            return EnsureVary(response, compressibleCandidate: true);
        }

        return CloneWithBody(response, compressed, encoding);
    }

    /// <summary>
    /// Compress a <see cref="ElsieHttpResponse.BodyWriter"/> response by wrapping the
    /// outgoing stream in a Brotli/GZip stream. Framing stays protocol-specific: HTTP/1.1
    /// drops Content-Length and is sent chunked by the connection handler; HTTP/2 and HTTP/3
    /// send DATA frames as today. SSE is intentionally left uncompressed (see below).
    /// </summary>
    private static ElsieHttpResponse MaybeCompressStreaming(
        ElsieRequest request,
        ElsieHttpResponse response,
        int minBodyBytes)
    {
        if (response.Headers.Contains("Content-Encoding"))
        {
            // Never double-compress (app already encoded the payload).
            return EnsureVary(response, compressibleCandidate: true);
        }

        var ct = response.ContentType ?? string.Empty;
        if (!IsCompressible(ct))
        {
            return response;
        }

        // SSE is delivered incrementally: each event flushes the wire, and flushing a
        // compression stream per event is pathological (Brotli/GZip flush semantics do not
        // guarantee the event bytes are visible to the client). Keeping `text/event-stream`
        // uncompressed preserves per-event delivery semantics.
        if (ct.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return EnsureVary(response, compressibleCandidate: true);
        }

        var encoding = ChooseEncoding(request.GetHeader("Accept-Encoding"));
        if (encoding is null)
        {
            // Compressible response left uncompressed — advertise Vary for caches.
            return EnsureVary(response, compressibleCandidate: true);
        }

        // The min-size threshold only applies when the length is known up front.
        // Unknown length (true streaming) → compress whenever negotiated.
        if (response.Headers.TryGetValues("Content-Length", out var contentLengths) &&
            contentLengths.Count > 0 &&
            long.TryParse(contentLengths[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared) &&
            declared < minBodyBytes)
        {
            return EnsureVary(response, compressibleCandidate: true);
        }

        var original = response.BodyWriter!;
        Func<Stream, CancellationToken, Task> wrapped = async (target, ct) =>
        {
            // leaveOpen: the wire stream is owned by the connection. Dispose (await using)
            // writes the final compressed block before the pipeline completes.
            await using var compression = encoding == "br"
                ? (Stream)new BrotliStream(target, CompressionLevel.Fastest, leaveOpen: true)
                : new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true);
            await original(compression, ct).ConfigureAwait(false);
        };

        var headers = new ElsieHeaders();
        headers.MergeFrom(response.Headers);
        headers.Set("Content-Encoding", encoding);
        headers.Remove("Content-Length");
        AddVary(headers);

        return ElsieHttpResponse.Create(
            response.StatusCode,
            response.ContentType,
            headers,
            body: null,
            bodyWriter: wrapped,
            webSocketHandler: null,
            trailers: response.Trailers);
    }

    /// <summary>
    /// Pick best accepted encoding with q &gt; 0. Prefers br over gzip.
    /// </summary>
    internal static string? ChooseEncoding(string? acceptEncoding)
    {
        if (string.IsNullOrWhiteSpace(acceptEncoding))
        {
            return null;
        }

        double brQ = -1;
        double gzipQ = -1;
        double starQ = -1;

        foreach (var raw in acceptEncoding.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var semi = raw.IndexOf(';');
            var coding = (semi >= 0 ? raw[..semi] : raw).Trim();
            var q = 1.0;
            if (semi >= 0)
            {
                var paramsPart = raw[(semi + 1)..];
                foreach (var p in paramsPart.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (p.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(p[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        // RFC 9110 §12.5.3: q-values are clamped to [0, 1].
                        q = Math.Clamp(parsed, 0.0, 1.0);
                    }
                }
            }

            if (coding.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                brQ = q;
            }
            else if (coding.Equals("gzip", StringComparison.OrdinalIgnoreCase) ||
                     coding.Equals("x-gzip", StringComparison.OrdinalIgnoreCase))
            {
                gzipQ = q;
            }
            else if (coding == "*")
            {
                starQ = q;
            }

        }

        // Explicit q=0 means not acceptable.
        if (brQ < 0 && starQ > 0)
        {
            brQ = starQ;
        }

        if (gzipQ < 0 && starQ > 0)
        {
            gzipQ = starQ;
        }

        if (brQ > 0 && brQ >= gzipQ)
        {
            return "br";
        }

        if (gzipQ > 0)
        {
            return "gzip";
        }

        // identity only / all q=0 → no compression
        return null;
    }

    private static bool IsCompressible(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        return contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
               || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
               || contentType.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] CompressGzip(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(data);
        }

        return ms.ToArray();
    }

    private static byte[] CompressBrotli(ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            br.Write(data);
        }

        return ms.ToArray();
    }

    private static ElsieHttpResponse EnsureVary(ElsieHttpResponse response, bool compressibleCandidate)
    {
        if (!compressibleCandidate)
        {
            return response;
        }

        if (response.Headers.TryGetValues("Vary", out var vary) &&
            vary.Any(v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
        {
            return response;
        }

        var headers = new ElsieHeaders();
        headers.MergeFrom(response.Headers);
        AddVary(headers);

        // `response.Body?.ToArray()` yields a null byte[]; converting a null byte[] variable
        // to ReadOnlyMemory<byte>? produces an EMPTY memory (Length 0), not null — which would
        // make writers treat a BodyWriter-only response as a zero-length buffered body.
        // Preserve the distinction explicitly.
        ReadOnlyMemory<byte>? body = null;
        if (response.Body is { } existing)
        {
            body = existing.ToArray();
        }

        return ElsieHttpResponse.Create(
            response.StatusCode,
            response.ContentType,
            headers,
            body,
            response.BodyWriter,
            response.WebSocketHandler,
            response.Trailers);
    }

    /// <summary>Advertise <c>Vary: Accept-Encoding</c> unless already present.</summary>
    private static void AddVary(ElsieHeaders headers)
    {
        if (headers.TryGetValues("Vary", out var vary) &&
            vary.Any(v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (headers.TryGetValues("Vary", out _))
        {
            headers.Add("Vary", "Accept-Encoding");
        }
        else
        {
            headers.Set("Vary", "Accept-Encoding");
        }
    }

    private static ElsieHttpResponse CloneWithBody(ElsieHttpResponse original, byte[] body, string encoding)
    {
        var headers = new ElsieHeaders();
        headers.MergeFrom(original.Headers);
        headers.Set("Content-Encoding", encoding);
        headers.Remove("Content-Length");
        AddVary(headers);

        return ElsieHttpResponse.Create(
            original.StatusCode,
            original.ContentType,
            headers,
            body,
            bodyWriter: null,
            webSocketHandler: null,
            trailers: original.Trailers);
    }
}
