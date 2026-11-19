using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Elsie.Web.Http;

namespace Elsie.Web.Hosting;

internal static class StaticFileHandler
{
    public static ElsieResult? TryServe(
        string method,
        string path,
        IReadOnlyDictionary<string, string> headers,
        ElsieStaticFileOptions options,
        string contentRoot)
    {
        if (!HttpMethods.IsGetOrHead(method))
        {
            return null;
        }

        var requestPath = NormalizePrefix(options.RequestPath);
        string relative;
        if (requestPath.Length == 0)
        {
            relative = path.TrimStart('/');
        }
        else
        {
            if (!path.StartsWith(requestPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (path.Length > requestPath.Length &&
                path[requestPath.Length] != '/' &&
                !string.Equals(path, requestPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            relative = path.Length <= requestPath.Length
                ? string.Empty
                : path[(requestPath.Length + (path[requestPath.Length] == '/' ? 1 : 0))..].TrimStart('/');
        }

        if (relative.Contains("..", StringComparison.Ordinal) ||
            relative.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return ElsieResult.BadRequest("Invalid path.");
        }

        var root = options.Root;
        root = Path.IsPathRooted(root)
            ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(options.ContentRoot ?? contentRoot, root));

        if (!Directory.Exists(root) || string.IsNullOrEmpty(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        // Require directory boundary — StartsWith(root) alone allows sibling prefix escapes
        // (root /var/www vs /var/www-evil).
        if (!IsPathInsideRoot(full, root))
        {
            return null;
        }

        if (!File.Exists(full))
        {
            return TrySpaFallback(path, headers, options, root);
        }

        // Precompressed variants (.br preferred over .gz) are served only when they are at least
        // as new as the original and the request is not a range request.
        var servingFile = full;
        string? contentEncoding = null;
        if (options.EnablePrecompressedFiles && !headers.ContainsKey("Range"))
        {
            var acceptEncoding = headers.TryGetValue("Accept-Encoding", out var ae) ? ae : string.Empty;
            var brotli = full + ".br";
            var gzip = full + ".gz";
            var originalWrite = File.GetLastWriteTimeUtc(full);
            if (acceptEncoding.Contains("br", StringComparison.OrdinalIgnoreCase)
                && File.Exists(brotli)
                && File.GetLastWriteTimeUtc(brotli) >= originalWrite)
            {
                servingFile = brotli;
                contentEncoding = "br";
            }
            else if (acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase)
                && File.Exists(gzip)
                && File.GetLastWriteTimeUtc(gzip) >= originalWrite)
            {
                servingFile = gzip;
                contentEncoding = "gzip";
            }
        }

        var info = new FileInfo(servingFile);
        var etag = ComputeEtag(info);
        if (contentEncoding is not null)
        {
            etag = etag[..^1] + "-" + contentEncoding + "\"";
        }
        var lastModified = info.LastWriteTimeUtc;

        if (headers.TryGetValue("If-None-Match", out var inm) && EtagMatches(inm, etag))
        {
            return ElsieResult.NotModified().WithHeader("ETag", etag);
        }

        if (headers.TryGetValue("If-Modified-Since", out var ims) &&
            DateTimeOffset.TryParse(ims, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since) &&
            lastModified <= since.UtcDateTime.AddSeconds(1))
        {
            return ElsieResult.NotModified()
                .WithHeader("ETag", etag)
                .WithHeader("Last-Modified", lastModified.ToString("R", CultureInfo.InvariantCulture));
        }

        var contentType = ContentTypes.FromExtension(full) ?? "application/octet-stream";
        long rangeStart = 0;
        long rangeLength = info.Length;
        var status = 200;

        if (headers.TryGetValue("Range", out var rangeHeader) &&
            TryParseBytesRange(rangeHeader, info.Length, out rangeStart, out rangeLength))
        {
            status = 206;
        }

        var start = rangeStart;
        var length = rangeLength;
        var isHead = HttpMethods.IsHead(method);

        ElsieResult result;
        if (isHead)
        {
            result = ElsieResult.Bytes(ReadOnlyMemory<byte>.Empty, contentType, status)
                .WithHeader("Content-Length", length.ToString(CultureInfo.InvariantCulture))
                .WithHeader("Accept-Ranges", "bytes")
                .WithHeader("ETag", etag)
                .WithHeader("Last-Modified", lastModified.ToString("R", CultureInfo.InvariantCulture));
        }
        else
        {
            result = ElsieResult.Stream(async (stream, ct) =>
            {
                await using var fs = new FileStream(
                    servingFile,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (start > 0)
                {
                    fs.Seek(start, SeekOrigin.Begin);
                }

                var buffer = new byte[64 * 1024];
                var remaining = length;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = await fs.ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    remaining -= read;
                }
            }, contentType, status)
                .WithHeader("Content-Length", length.ToString(CultureInfo.InvariantCulture))
                .WithHeader("Accept-Ranges", "bytes")
                .WithHeader("ETag", etag)
                .WithHeader("Last-Modified", lastModified.ToString("R", CultureInfo.InvariantCulture));

            if (status == 206)
            {
                result = result.WithHeader(
                    "Content-Range",
                    $"bytes {start}-{start + length - 1}/{info.Length}");
            }
        }

        if (contentEncoding is not null)
        {
            result = result
                .WithHeader("Content-Encoding", contentEncoding)
                .WithHeader("Vary", "Accept-Encoding");
        }

        if (options.MaxAge is { } maxAge)
        {
            result = result.WithHeader("Cache-Control", $"public, max-age={(int)maxAge.TotalSeconds}");
        }

        if (options.ImmutableFingerprintedAssets && HasContentHash(full))
        {
            result = result.WithHeader(
                "Cache-Control",
                $"public, max-age={(int)options.ImmutableMaxAge.TotalSeconds}, immutable");
        }

        return result;
    }

    /// <summary>
    /// SPA fallback: extension-less GET/HEAD requests that would prefer HTML get the configured
    /// document instead of a 404. Missing assets (paths with an extension) still 404 so errors are
    /// not masked by index.html.
    /// </summary>
    private static ElsieResult? TrySpaFallback(
        string path,
        IReadOnlyDictionary<string, string> headers,
        ElsieStaticFileOptions options,
        string root)
    {
        if (string.IsNullOrWhiteSpace(options.SpaFallback) || headers.ContainsKey("Range"))
        {
            return null;
        }

        if (Path.HasExtension(path))
        {
            return null;
        }

        var accept = headers.TryGetValue("Accept", out var value) ? value : "text/html";
        if (!accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            && !accept.Contains("*/*", StringComparison.Ordinal))
        {
            return null;
        }

        var fallback = Path.GetFullPath(Path.Combine(root, options.SpaFallback));
        if (!IsPathInsideRoot(fallback, root) || !File.Exists(fallback))
        {
            return null;
        }

        var contentType = ContentTypes.FromExtension(fallback) ?? "text/html; charset=utf-8";
        return ElsieResult.File(File.ReadAllBytes(fallback), contentType)
            .WithHeader("Cache-Control", "no-cache");
    }

    /// <summary>Content-addressed names such as <c>app.4f3a9c1d.js</c> can be cached forever.</summary>
    private static bool HasContentHash(string fullPath)
    {
        var parts = Path.GetFileName(fullPath).Split('.');
        for (var i = 1; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (part.Length is < 8 or > 64)
            {
                continue;
            }

            var hasDigit = false;
            var valid = true;
            foreach (var c in part)
            {
                if (char.IsAsciiDigit(c))
                {
                    hasDigit = true;
                }
                else if (!char.IsAsciiLetter(c))
                {
                    valid = false;
                    break;
                }
            }

            if (valid && hasDigit)
            {
                return true;
            }
        }

        return false;
    }

    private static string ComputeEtag(FileInfo info)
    {
        var raw = $"{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "\"" + Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant() + "\"";
    }

    private static bool EtagMatches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        foreach (var part in ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "*")
            {
                return true;
            }

            var candidate = part.StartsWith("W/", StringComparison.Ordinal) ? part[2..].Trim() : part;
            if (string.Equals(candidate, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseBytesRange(string header, long length, out long start, out long count)
    {
        start = 0;
        count = length;
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var spec = header["bytes=".Length..].Trim();
        // single range only
        if (spec.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return false;
        }

        var fromText = spec[..dash];
        var toText = spec[(dash + 1)..];
        if (fromText.Length == 0 && long.TryParse(toText, out var suffix) && suffix > 0)
        {
            start = Math.Max(0, length - suffix);
            count = length - start;
            return count > 0;
        }

        if (!long.TryParse(fromText, out start) || start < 0 || start >= length)
        {
            return false;
        }

        long end = length - 1;
        if (toText.Length > 0)
        {
            if (!long.TryParse(toText, out end) || end < start)
            {
                return false;
            }

            end = Math.Min(end, length - 1);
        }

        count = end - start + 1;
        return count > 0;
    }

    private static string NormalizePrefix(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath) || requestPath == "/")
        {
            return string.Empty;
        }

        return requestPath.StartsWith('/') ? requestPath.TrimEnd('/') : "/" + requestPath.TrimEnd('/');
    }

    internal static bool IsPathInsideRoot(string fullPath, string root)
    {
        var rootFull = Path.GetFullPath(root);
        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(fullPath, rootFull, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class HttpMethods
{
    public static bool IsGetOrHead(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public static bool IsHead(string method) =>
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public static bool IsOptions(string method) =>
        method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);
}
