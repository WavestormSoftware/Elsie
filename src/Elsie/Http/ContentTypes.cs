namespace Elsie.Web.Http;

internal static class ContentTypes
{
    public static string? FromExtension(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0)
        {
            return null;
        }

        return ext.ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".mjs" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".txt" => "text/plain; charset=utf-8",
            ".xml" => "application/xml",
            ".pdf" => "application/pdf",
            ".map" => "application/json",
            _ => "application/octet-stream"
        };
    }
}
