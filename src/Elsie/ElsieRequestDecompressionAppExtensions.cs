using Elsie.RequestDecompression;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web;

/// <summary>App-level registration for inbound request body decompression.</summary>
public static class ElsieRequestDecompressionAppExtensions
{
    /// <summary>
    /// Enable inbound request body decompression (<c>gzip</c>/<c>deflate</c>/<c>br</c>, stacked
    /// codings decoded in reverse application order). Unsupported codings → <c>415</c>; a decoded
    /// body exceeding <see cref="ElsieRequestDecompressionOptions.MaxDecompressedBodySize"/>
    /// (default 10 MiB) → <c>413 Payload Too Large</c> mid-stream. Requests without a
    /// <c>Content-Encoding</c> header pass through untouched.
    /// </summary>
    public static ElsieApp UseRequestDecompression(
        this ElsieApp app,
        Action<ElsieRequestDecompressionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services(s => s.AddRequestDecompression(configure));
    }
}