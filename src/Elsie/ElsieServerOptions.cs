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

    /// <summary>
    /// When true (default), send <c>100 Continue</c> for requests with <c>Expect: 100-continue</c>
    /// before reading the body. Set false to disable.
    /// </summary>
    public bool DisableContinue { get; set; }

    /// <summary>
    /// When true (default), cancel <see cref="ElsieRequest.RequestAborted"/> when the client
    /// disconnects mid-handler (HTTP/1.1).
    /// </summary>
    public bool AbortRequestsOnClientDisconnect { get; set; } = true;

    /// <summary>
    /// When true (default), force-close remaining sockets after <see cref="ConnectionDrainTimeout"/>
    /// on shutdown.
    /// </summary>
    public bool ShutdownAbortConnections { get; set; } = true;

    /// <summary>Max concurrent accepted connections (default 10_000).</summary>
    public int MaxConcurrentConnections { get; set; } = 10_000;

    /// <summary>How long <see cref="Hosting.ElsieServer.StopAsync"/> waits for in-flight connections.</summary>
    public TimeSpan ConnectionDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>TCP listen backlog (default 512).</summary>
    public int ListenBacklog { get; set; } = 512;

    /// <summary>Max concurrent HTTP/2 streams per connection.</summary>
    public int MaxConcurrentStreams { get; set; } = 100;

    /// <summary>
    /// Dynamic-table capacity advertised to HTTP/3 clients via SETTINGS_QPACK_MAX_TABLE_CAPACITY
    /// (bounds the memory the client's QPACK encoder can use against us; RFC 9204 §5).
    /// A value of 0 keeps the capacity-0 interop path (no client dynamic inserts). Default 4096.
    /// </summary>
    public int QpackMaxTableCapacity { get; set; } = 4096;

    /// <summary>
    /// Max simultaneously blocked HTTP/3 request streams we promise to support
    /// (SETTINGS_QPACK_BLOCKED_STREAMS; RFC 9204 §2.1.2). Default 100.
    /// </summary>
    public int QpackBlockedStreams { get; set; } = 100;

    /// <summary>
    /// Max inbound unidirectional QUIC streams per HTTP/3 connection (client control + QPACK
    /// encoder/decoder + push/unknown streams, RFC 9114 §6.2). Default 10.
    /// </summary>
    public int Http3MaxInboundUnidirectionalStreams { get; set; } = 10;

    /// <summary>
    /// Max bytes of an HTTP/3 request field section we accept (advertised via
    /// SETTINGS_MAX_FIELD_SECTION_SIZE, RFC 9114 §7.2.4.2; larger sections are an
    /// H3_EXCESSIVE_LOAD connection error). Default 16 KiB.
    /// </summary>
    public int Http3MaxFieldSectionBytes { get; set; } = 16 * 1024;

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

    /// <summary>
    /// When true, emit structured per-request log lines via <see cref="Microsoft.Extensions.Logging.ILogger"/>.
    /// Default true when a non-null logger factory is configured on the host.
    /// </summary>
    public bool LogRequests { get; set; } = true;

    /// <summary>
    /// Enable OS-level TCP keepalive on accepted sockets (default true).
    /// </summary>
    public bool TcpKeepAlive { get; set; } = true;

    /// <summary>Idle time before the first TCP keepalive probe (default 2 minutes).</summary>
    public TimeSpan TcpKeepAliveTime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Interval between TCP keepalive probes (default 1 minute).</summary>
    public TimeSpan TcpKeepAliveInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Close keep-alive connections that sit idle between requests longer than this.
    /// Default <see cref="TimeSpan.Zero"/> = off (use <see cref="RequestHeadersTimeout"/> only).
    /// </summary>
    public TimeSpan ConnectionIdleTimeout { get; set; }

    /// <summary>Copies all values from <paramref name="source"/> onto this instance (reload plumbing).</summary>
    internal void CopyFrom(ElsieServerOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        MaxRequestLineLength = source.MaxRequestLineLength;
        MaxHeaderBytes = source.MaxHeaderBytes;
        MaxRequestBodyBytes = source.MaxRequestBodyBytes;
        RequestHeadersTimeout = source.RequestHeadersTimeout;
        RequestBodyIdleTimeout = source.RequestBodyIdleTimeout;
        DisableContinue = source.DisableContinue;
        AbortRequestsOnClientDisconnect = source.AbortRequestsOnClientDisconnect;
        ShutdownAbortConnections = source.ShutdownAbortConnections;
        MaxConcurrentConnections = source.MaxConcurrentConnections;
        ConnectionDrainTimeout = source.ConnectionDrainTimeout;
        ListenBacklog = source.ListenBacklog;
        MaxConcurrentStreams = source.MaxConcurrentStreams;
        QpackMaxTableCapacity = source.QpackMaxTableCapacity;
        QpackBlockedStreams = source.QpackBlockedStreams;
        Http3MaxInboundUnidirectionalStreams = source.Http3MaxInboundUnidirectionalStreams;
        Http3MaxFieldSectionBytes = source.Http3MaxFieldSectionBytes;
        MaxFrameSize = source.MaxFrameSize;
        UseForwardedHeaders = source.UseForwardedHeaders;
        EnableResponseCompression = source.EnableResponseCompression;
        CompressionMinBodyBytes = source.CompressionMinBodyBytes;
        LogRequests = source.LogRequests;
        TcpKeepAlive = source.TcpKeepAlive;
        TcpKeepAliveTime = source.TcpKeepAliveTime;
        TcpKeepAliveInterval = source.TcpKeepAliveInterval;
        ConnectionIdleTimeout = source.ConnectionIdleTimeout;
    }
}
