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
        if (response.BodyWriter is not null || response.WebSocketHandler is not null)
        {
            return response;
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
        var sawIdentity = false;
        double identityQ = 1;

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
                        q = parsed;
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
            else if (coding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            {
                sawIdentity = true;
                identityQ = q;
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
        _ = sawIdentity;
        _ = identityQ;
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
        if (headers.TryGetValues("Vary", out _))
        {
            headers.Add("Vary", "Accept-Encoding");
        }
        else
        {
            headers.Set("Vary", "Accept-Encoding");
        }

        return ElsieHttpResponse.Create(
            response.StatusCode,
            response.ContentType,
            headers,
            response.Body?.ToArray(),
            response.BodyWriter,
            response.WebSocketHandler);
    }

    private static ElsieHttpResponse CloneWithBody(ElsieHttpResponse original, byte[] body, string encoding)
    {
        var headers = new ElsieHeaders();
        headers.MergeFrom(original.Headers);
        headers.Set("Content-Encoding", encoding);
        headers.Remove("Content-Length");
        if (headers.TryGetValues("Vary", out var vary) && vary.Count > 0)
        {
            if (!vary.Any(v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add("Vary", "Accept-Encoding");
            }
        }
        else
        {
            headers.Set("Vary", "Accept-Encoding");
        }

        return ElsieHttpResponse.Create(
            original.StatusCode,
            original.ContentType,
            headers,
            body,
            bodyWriter: null,
            webSocketHandler: null);
    }
}
