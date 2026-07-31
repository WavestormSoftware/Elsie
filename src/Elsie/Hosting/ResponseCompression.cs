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
            return response;
        }

        if (response.Headers.Contains("Content-Encoding"))
        {
            return response;
        }

        var ct = response.ContentType ?? string.Empty;
        if (!IsCompressible(ct))
        {
            return response;
        }

        var accept = request.GetHeader("Accept-Encoding") ?? string.Empty;
        if (accept.Contains("br", StringComparison.OrdinalIgnoreCase))
        {
            var compressed = CompressBrotli(body.Span);
            if (compressed.Length < body.Length)
            {
                return CloneWithBody(response, compressed, "br");
            }
        }

        if (accept.Contains("gzip", StringComparison.OrdinalIgnoreCase))
        {
            var compressed = CompressGzip(body.Span);
            if (compressed.Length < body.Length)
            {
                return CloneWithBody(response, compressed, "gzip");
            }
        }

        return response;
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

    private static ElsieHttpResponse CloneWithBody(ElsieHttpResponse original, byte[] body, string encoding)
    {
        var headers = new ElsieHeaders();
        headers.MergeFrom(original.Headers);
        headers.Set("Content-Encoding", encoding);
        headers.Remove("Content-Length");
        // Vary
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
