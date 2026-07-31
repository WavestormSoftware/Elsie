using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Elsie.Web.Http;

namespace Elsie.Web.Hosting;

internal static class StaticFileHandler
{
    public static ElsieHttpResponse? TryServe(
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
            return FromResult(ElsieResult.BadRequest("Invalid path."));
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
        if (!IsPathInsideRoot(full, root) || !File.Exists(full))
        {
            return null;
        }

        var info = new FileInfo(full);
        var etag = ComputeEtag(info);
        var lastModified = info.LastWriteTimeUtc;

        if (headers.TryGetValue("If-None-Match", out var inm) && EtagMatches(inm, etag))
        {
            return FromResult(ElsieResult.NotModified().WithHeader("ETag", etag));
        }

        if (headers.TryGetValue("If-Modified-Since", out var ims) &&
            DateTimeOffset.TryParse(ims, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since) &&
            lastModified <= since.UtcDateTime.AddSeconds(1))
        {
            return FromResult(ElsieResult.NotModified()
                .WithHeader("ETag", etag)
                .WithHeader("Last-Modified", lastModified.ToString("R", CultureInfo.InvariantCulture)));
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
                    full,
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

        if (options.MaxAge is { } maxAge)
        {
            result = result.WithHeader("Cache-Control", $"public, max-age={(int)maxAge.TotalSeconds}");
        }

        return FromResult(result);
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

    private static ElsieHttpResponse FromResult(ElsieResult result) =>
        ElsieHttpResponse.FromDispatch(ElsieDispatchResult.Handled(result, new ElsieResponse()))!;

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
