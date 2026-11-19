using System.Security.Cryptography;
using System.Text;

namespace Elsie.Web.Http;

internal static class WebSocketUpgrade
{
    private const string Magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static bool IsUpgradeRequest(ParsedHttpRequest request)
    {
        if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var upgrade = First(request.Headers, "Upgrade");
        var connection = First(request.Headers, "Connection");
        var key = First(request.Headers, "Sec-WebSocket-Key");
        return !string.IsNullOrEmpty(key) &&
               upgrade is not null &&
               upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase) &&
               connection is not null &&
               connection.Contains("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeAccept(string secWebSocketKey)
    {
        var bytes = Encoding.ASCII.GetBytes(secWebSocketKey.Trim() + Magic);
        var hash = SHA1.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Pick the first server-supported subprotocol that the client offered (header order is client
    /// preference). Returns null when the client offered nothing usable.
    /// </summary>
    public static string? SelectSubprotocol(
        Dictionary<string, List<string>> requestHeaders,
        IReadOnlyList<string> supported)
    {
        ArgumentNullException.ThrowIfNull(requestHeaders);
        ArgumentNullException.ThrowIfNull(supported);
        if (supported.Count == 0 || !requestHeaders.TryGetValue("Sec-WebSocket-Protocol", out var values))
        {
            return null;
        }

        foreach (var raw in values)
        {
            foreach (var candidate in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var protocol in supported)
                {
                    if (string.Equals(candidate, protocol, StringComparison.Ordinal))
                    {
                        return protocol;
                    }
                }
            }
        }

        return null;
    }

    public static async Task WriteHandshakeAsync(
        Stream stream,
        string protocol,
        string secWebSocketKey,
        CancellationToken cancellationToken,
        string? subprotocol = null)
    {
        var accept = ComputeAccept(secWebSocketKey);
        var negotiated = string.IsNullOrEmpty(subprotocol)
            ? string.Empty
            : $"Sec-WebSocket-Protocol: {subprotocol}\r\n";
        var response =
            $"{protocol} 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            negotiated +
            "\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? First(Dictionary<string, List<string>> headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
