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

    public static async Task WriteHandshakeAsync(
        Stream stream,
        string protocol,
        string secWebSocketKey,
        CancellationToken cancellationToken)
    {
        var accept = ComputeAccept(secWebSocketKey);
        var response =
            $"{protocol} 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n" +
            "\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? First(Dictionary<string, List<string>> headers, string name) =>
        headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;
}
