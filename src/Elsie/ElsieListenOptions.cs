using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Elsie.Web;

/// <summary>One listen endpoint (cleartext or TLS).</summary>
public sealed class ElsieListenOptions
{
    public IPAddress Address { get; set; } = IPAddress.Loopback;
    public int Port { get; set; } = 5000;
    public bool UseHttps { get; set; }
    public X509Certificate2? Certificate { get; set; }
    public ElsieHttpProtocols Protocols { get; set; } = ElsieHttpProtocols.Http1;

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
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid listen URL '{url}'.", nameof(url));
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Listen URL must be http or https: '{url}'.", nameof(url));
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
