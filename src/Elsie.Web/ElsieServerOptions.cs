namespace Elsie.Web;

/// <summary>Server-wide limits and timeouts for the custom host.</summary>
public sealed class ElsieServerOptions
{
    /// <summary>Max HTTP/1.1 request line length in bytes.</summary>
    public int MaxRequestLineLength { get; set; } = 8 * 1024;

    /// <summary>Max HTTP/1.1 header block size in bytes.</summary>
    public int MaxHeaderBytes { get; set; } = 32 * 1024;

    /// <summary>Max request body size in bytes (HTTP/1.1 and HTTP/2 DATA).</summary>
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Idle read timeout per connection (headers/body).</summary>
    public TimeSpan RequestHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max concurrent HTTP/2 streams per connection.</summary>
    public int MaxConcurrentStreams { get; set; } = 100;

    /// <summary>Max HTTP/2 frame payload size we accept.</summary>
    public int MaxFrameSize { get; set; } = 16384;
}
