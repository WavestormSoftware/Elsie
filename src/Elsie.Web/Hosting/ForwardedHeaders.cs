namespace Elsie.Web.Hosting;

/// <summary>
/// Applies <c>X-Forwarded-For</c>, <c>X-Forwarded-Proto</c>, and <c>X-Forwarded-Host</c>
/// when the host is behind a trusted reverse proxy.
/// </summary>
/// <summary>Visible to tests for injection-safety unit coverage.</summary>
public static class ForwardedHeaders
{
    public static (string? Scheme, string? Host, string? RemoteIp) Apply(
        bool enabled,
        string? scheme,
        string? host,
        string? remoteIp,
        Func<string, string?> getHeader)
    {
        if (!enabled)
        {
            return (scheme, host, remoteIp);
        }

        var proto = getHeader("X-Forwarded-Proto");
        if (!string.IsNullOrWhiteSpace(proto))
        {
            // First value if comma-separated
            var p = proto.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length > 0 &&
                (p[0].Equals("http", StringComparison.OrdinalIgnoreCase) ||
                 p[0].Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                scheme = p[0].ToLowerInvariant();
            }
        }

        var fwdHost = getHeader("X-Forwarded-Host");
        if (!string.IsNullOrWhiteSpace(fwdHost))
        {
            var h = fwdHost.Split(',', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (h.Length > 0 && h[0].Length > 0 && h[0].IndexOfAny(['\r', '\n', ' ']) < 0)
            {
                host = h[0];
            }
        }

        var fwdFor = getHeader("X-Forwarded-For");
        if (!string.IsNullOrWhiteSpace(fwdFor))
        {
            // Left-most is original client when proxies append
            var parts = fwdFor.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Length > 0 && parts[0].IndexOfAny(['\r', '\n']) < 0)
            {
                remoteIp = parts[0];
            }
        }

        return (scheme, host, remoteIp);
    }
}
