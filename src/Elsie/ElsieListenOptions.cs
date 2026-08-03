using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Elsie.Web;

/// <summary>One listen endpoint (cleartext or TLS).</summary>
public sealed class ElsieListenOptions
{
    public IPAddress Address { get; set; } = IPAddress.Loopback;
    public int Port { get; set; } = 5000;

    /// <summary>
    /// When set, listen on a Unix domain socket at this path (HTTP/1.1 only; TLS/ALPN n/a).
    /// Takes precedence over <see cref="Address"/>/<see cref="Port"/>.
    /// </summary>
    public string? UnixSocketPath { get; set; }

    public bool UseHttps { get; set; }
    public X509Certificate2? Certificate { get; set; }
    public ElsieHttpProtocols Protocols { get; set; } = ElsieHttpProtocols.Http1;

    /// <summary>
    /// When true, also listen for HTTP/3 on a UDP socket with the same address/port
    /// (requires TLS + ALPN <c>h3</c>, i.e. <see cref="UseHttps"/> and <see cref="Certificate"/>).
    /// Skipped silently when <c>QuicListener.IsSupported</c> is false (e.g. no libmsquic).
    /// </summary>
    public bool EnableHttp3 { get; set; }

    public bool IsUnixSocket => !string.IsNullOrWhiteSpace(UnixSocketPath);

    /// <summary>Create a cleartext HTTP/1.1 Unix domain socket endpoint.</summary>
    public static ElsieListenOptions FromUnixSocketPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new ElsieListenOptions
        {
            UnixSocketPath = path,
            UseHttps = false,
            Protocols = ElsieHttpProtocols.Http1
        };
    }

    /// <summary>Load PEM certificate + private key for HTTPS.</summary>
    public ElsieListenOptions CertificateFromPem(string certificatePath, string privateKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPath);
        Certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
        UseHttps = true;
        return this;
    }

    /// <summary>Load a PFX/PKCS#12 certificate.</summary>
    public ElsieListenOptions CertificateFromPfx(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        UseHttps = true;
        return this;
    }

    public ElsieListenOptions WithProtocols(ElsieHttpProtocols protocols)
    {
        Protocols = protocols;
        return this;
    }

    public static ElsieListenOptions Parse(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        // http+unix:///tmp/elsie.sock  or  http://unix:/tmp/elsie.sock
        if (url.StartsWith("http+unix:", StringComparison.OrdinalIgnoreCase))
        {
            var path = url["http+unix:".Length..];
            if (path.StartsWith("//", StringComparison.Ordinal))
            {
                path = path[1..]; // keep single leading /
            }

            // Windows drive-letter paths (http+unix://C:\tmp\x.sock): the "/" kept above is
            // drive-relative ("D:\C:\..." after GetFullPath) — drop it.
            if (path.Length > 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':' && OperatingSystem.IsWindows())
            {
                path = path[1..];
            }

            if (string.IsNullOrWhiteSpace(path) || path == "/")
            {
                throw new ArgumentException($"Unix socket path missing in '{url}'.", nameof(url));
            }

            return FromUnixSocketPath(path);
        }

        if (url.StartsWith("http://unix:", StringComparison.OrdinalIgnoreCase))
        {
            var path = url["http://unix:".Length..];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException($"Unix socket path missing in '{url}'.", nameof(url));
            }

            return FromUnixSocketPath(path);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid listen URL '{url}'.", nameof(url));
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Listen URL must be http, https, or http+unix: '{url}'.", nameof(url));
        }

        var https = string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        var host = uri.Host;
        IPAddress address;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
        }
        else if (host is "+" or "*" or "0.0.0.0")
        {
            address = IPAddress.Any;
        }
        else if (host is "[::]" or "::")
        {
            address = IPAddress.IPv6Any;
        }
        else if (!IPAddress.TryParse(host, out address!))
        {
            address = IPAddress.Any;
        }

        var port = uri.IsDefaultPort ? (https ? 443 : 80) : uri.Port;
        return new ElsieListenOptions
        {
            Address = address,
            Port = port,
            UseHttps = https,
            Protocols = ElsieHttpProtocols.Http1
        };
    }
}
