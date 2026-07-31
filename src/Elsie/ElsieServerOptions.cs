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

    /// <summary>Timeout to finish reading request headers (default 30s).</summary>
    public TimeSpan RequestHeadersTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Idle timeout while reading a request body (default 30s).</summary>
    public TimeSpan RequestBodyIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Max concurrent accepted connections (default 10_000).</summary>
    public int MaxConcurrentConnections { get; set; } = 10_000;

    /// <summary>How long <see cref="Hosting.ElsieServer.StopAsync"/> waits for in-flight connections.</summary>
    public TimeSpan ConnectionDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>TCP listen backlog (default 512).</summary>
    public int ListenBacklog { get; set; } = 512;

    /// <summary>Max concurrent HTTP/2 streams per connection.</summary>
    public int MaxConcurrentStreams { get; set; } = 100;

    /// <summary>Max HTTP/2 frame payload size we accept.</summary>
    public int MaxFrameSize { get; set; } = 16384;

    /// <summary>
    /// When true, honor <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>, and <c>X-Forwarded-Host</c>
    /// (use only behind a trusted reverse proxy).
    /// </summary>
    public bool UseForwardedHeaders { get; set; }

    /// <summary>Enable gzip/brotli response compression when the client accepts it.</summary>
    public bool EnableResponseCompression { get; set; }

    /// <summary>Minimum body size before compression is considered (default 1 KiB).</summary>
    public int CompressionMinBodyBytes { get; set; } = 1024;
}
