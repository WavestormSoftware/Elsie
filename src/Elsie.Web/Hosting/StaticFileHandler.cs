using Elsie.Web.Http;

namespace Elsie.Web.Hosting;

internal static class StaticFileHandler
{
    public static ElsieHttpResponse? TryServe(
        string method,
        string path,
        ElsieStaticFileOptions options,
        string contentRoot)
    {
        if (!HttpMethods.IsGetOrHead(method))
        {
            return null;
        }

        var requestPath = NormalizePrefix(options.RequestPath);
        string relative;
        if (requestPath.Length == 0)
        {
            relative = path.TrimStart('/');
        }
        else
        {
            if (!path.StartsWith(requestPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (path.Length > requestPath.Length &&
                path[requestPath.Length] != '/' &&
                !string.Equals(path, requestPath, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            relative = path.Length <= requestPath.Length
                ? string.Empty
                : path[(requestPath.Length + (path[requestPath.Length] == '/' ? 1 : 0))..].TrimStart('/');
        }

        if (relative.Contains("..", StringComparison.Ordinal) ||
            relative.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
        {
            return FromResult(ElsieResult.BadRequest("Invalid path."));
        }

        var root = options.Root;
        root = Path.IsPathRooted(root)
            ? Path.GetFullPath(root)
            : Path.GetFullPath(Path.Combine(options.ContentRoot ?? contentRoot, root));

        if (!Directory.Exists(root) || string.IsNullOrEmpty(relative))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(full);
        var contentType = ContentTypes.FromExtension(full) ?? "application/octet-stream";
        var result = ElsieResult.Bytes(bytes, contentType);
        if (options.MaxAge is { } maxAge)
        {
            result = result.WithHeader("Cache-Control", $"public, max-age={(int)maxAge.TotalSeconds}");
        }

        return FromResult(result);
    }

    private static ElsieHttpResponse FromResult(ElsieResult result) =>
        ElsieHttpResponse.FromDispatch(ElsieDispatchResult.Handled(result, new ElsieResponse()))!;

    private static string NormalizePrefix(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath) || requestPath == "/")
        {
            return string.Empty;
        }

        return requestPath.StartsWith('/') ? requestPath.TrimEnd('/') : "/" + requestPath.TrimEnd('/');
    }
}

internal static class HttpMethods
{
    public static bool IsGetOrHead(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public static bool IsHead(string method) =>
        method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public static bool IsOptions(string method) =>
        method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);
}
