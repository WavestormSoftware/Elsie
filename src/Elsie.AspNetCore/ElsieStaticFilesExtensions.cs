using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Elsie.AspNetCore;

/// <summary>
/// Lightweight static-file middleware for Elsie hosts.
/// Safe path resolution, common content types, weak ETag / Last-Modified + 304,
/// default document, HEAD support. No range requests.
/// </summary>
public static class ElsieStaticFilesExtensions
{
    private static readonly Dictionary<string, string> DefaultContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".bmp"] = "image/bmp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".eot"] = "application/vnd.ms-fontobject",
        [".mp3"] = "audio/mpeg",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".wasm"] = "application/wasm",
        [".csv"] = "text/csv; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
    };

    /// <summary>
    /// Serves files under <paramref name="requestPath"/> from <paramref name="contentRoot"/>.
    /// Unmatched / missing files fall through to the next middleware.
    /// Unsafe relative paths under the mount return 404 problem+json.
    /// </summary>
    public static WebApplication MapElsieStaticFiles(
        this WebApplication app,
        string requestPath,
        string contentRoot,
        Action<ElsieStaticFileOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ((IApplicationBuilder)app).MapElsieStaticFiles(requestPath, contentRoot, configure);
        return app;
    }

    /// <inheritdoc cref="MapElsieStaticFiles(WebApplication, string, string, Action{ElsieStaticFileOptions}?)"/>
    public static IApplicationBuilder MapElsieStaticFiles(
        this IApplicationBuilder app,
        string requestPath,
        string contentRoot,
        Action<ElsieStaticFileOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var options = new ElsieStaticFileOptions();
        configure?.Invoke(options);

        var mount = ElsieStaticPath.NormalizeMount(requestPath);
        var rootFull = Path.GetFullPath(contentRoot);
        if (!Directory.Exists(rootFull))
        {
            Directory.CreateDirectory(rootFull);
        }

        var contentTypes = new Dictionary<string, string>(DefaultContentTypes, StringComparer.OrdinalIgnoreCase);
        foreach (var (ext, type) in options.ContentTypes)
        {
            contentTypes[ext.StartsWith('.') ? ext : "." + ext] = type;
        }

        return app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                await next().ConfigureAwait(false);
                return;
            }

            var path = context.Request.Path.Value ?? "/";
            if (!ElsieStaticPath.IsUnderMount(path, mount, out var relative))
            {
                await next().ConfigureAwait(false);
                return;
            }

            relative = Uri.UnescapeDataString(relative).Replace('\\', '/');

            var candidateRelative = relative;
            if (string.IsNullOrEmpty(candidateRelative) || candidateRelative.EndsWith('/'))
            {
                if (!options.ServeDefaultFile)
                {
                    await next().ConfigureAwait(false);
                    return;
                }

                candidateRelative = string.IsNullOrEmpty(candidateRelative)
                    ? options.DefaultFileName
                    : candidateRelative + options.DefaultFileName;
            }

            if (!ElsieStaticPath.TryResolve(rootFull, candidateRelative, out var fullPath))
            {
                await WriteNotFoundAsync(context).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(fullPath))
            {
                // Missing file: fall through so other middleware / Elsie routes can answer.
                await next().ConfigureAwait(false);
                return;
            }

            var fileInfo = new FileInfo(fullPath);
            var lastModified = fileInfo.LastWriteTimeUtc;
            // Truncate to seconds for If-Modified-Since round-trip.
            lastModified = new DateTimeOffset(
                lastModified.Year,
                lastModified.Month,
                lastModified.Day,
                lastModified.Hour,
                lastModified.Minute,
                lastModified.Second,
                TimeSpan.Zero).UtcDateTime;

            var etag = CreateWeakEtag(lastModified, fileInfo.Length);
            var ifNoneMatch = context.Request.Headers.IfNoneMatch.ToString();
            if (EtagMatches(ifNoneMatch, etag))
            {
                await WriteNotModifiedAsync(context, etag, lastModified).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrEmpty(ifNoneMatch) &&
                context.Request.Headers.TryGetValue("If-Modified-Since", out var imsRaw) &&
                DateTimeOffset.TryParse(
                    imsRaw.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var ims) &&
                lastModified <= ims.UtcDateTime)
            {
                await WriteNotModifiedAsync(context, etag, lastModified).ConfigureAwait(false);
                return;
            }

            var contentType = ResolveContentType(fullPath, contentTypes);
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = contentType;
            response.ContentLength = fileInfo.Length;
            response.Headers.ETag = etag;
            response.Headers.LastModified = lastModified.ToString("R", CultureInfo.InvariantCulture);

            if (HttpMethods.IsHead(context.Request.Method))
            {
                return;
            }

            await using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await stream.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
        });
    }

    private static string ResolveContentType(string fullPath, Dictionary<string, string> map)
    {
        var ext = Path.GetExtension(fullPath);
        if (ext.Length > 0 && map.TryGetValue(ext, out var type))
        {
            return type;
        }

        return "application/octet-stream";
    }

    private static string CreateWeakEtag(DateTime lastModifiedUtc, long length) =>
        $"W/\"{lastModifiedUtc.Ticks:x}-{length:x}\"";

    private static bool EtagMatches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        var candidate = StripWeak(etag);
        foreach (var part in ifNoneMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "*")
            {
                return true;
            }

            if (string.Equals(StripWeak(part), candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripWeak(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            v = v[2..].Trim();
        }

        return v;
    }

    private static Task WriteNotModifiedAsync(HttpContext context, string etag, DateTime lastModifiedUtc)
    {
        var response = context.Response;
        response.StatusCode = StatusCodes.Status304NotModified;
        response.Headers.ETag = etag;
        response.Headers.LastModified = lastModifiedUtc.ToString("R", CultureInfo.InvariantCulture);
        return Task.CompletedTask;
    }

    private static async Task WriteNotFoundAsync(HttpContext context)
    {
        var baked = ElsieHttpResponse.FromDispatch(
            ElsieDispatchResult.Handled(ElsieResult.NotFound(), new ElsieResponse()))!;
        await AspNetCoreElsieResponseWriter.WriteAsync(context, baked, context.RequestAborted)
            .ConfigureAwait(false);
    }
}
