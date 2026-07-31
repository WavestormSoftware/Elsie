namespace Elsie.Web;

/// <summary>HTTP protocol versions the host may negotiate.</summary>
[Flags]
public enum ElsieHttpProtocols
{
    /// <summary>HTTP/1.1 only (default).</summary>
    Http1 = 1,

    /// <summary>HTTP/2 (requires TLS + ALPN in typical deployments).</summary>
    Http2 = 2,

    /// <summary>HTTP/1.1 and HTTP/2.</summary>
    Http1AndHttp2 = Http1 | Http2
}
