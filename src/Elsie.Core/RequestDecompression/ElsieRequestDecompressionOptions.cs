namespace Elsie.RequestDecompression;

/// <summary>
/// Options for <see cref="ElsieRequestDecompressionMiddleware"/> (inbound request body decompression).
/// </summary>
public sealed class ElsieRequestDecompressionOptions
{
    /// <summary>Default decompressed body cap: 10 MiB.</summary>
    public const long DefaultMaxDecompressedBodySize = 10 * 1024 * 1024;

    /// <summary>
    /// Maximum number of decompressed request body bytes accepted. When a decoded body exceeds
    /// this cap mid-stream the request fails with <c>413 Payload Too Large</c> (decompression-bomb
    /// protection). Defaults to <see cref="DefaultMaxDecompressedBodySize"/> (10 MiB).
    /// </summary>
    public long MaxDecompressedBodySize { get; set; } = DefaultMaxDecompressedBodySize;
}
