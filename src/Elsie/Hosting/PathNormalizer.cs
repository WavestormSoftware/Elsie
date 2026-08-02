namespace Elsie.Web.Hosting;

/// <summary>
/// Canonicalizes request paths at the host boundary (no filesystem, no percent-decode).
/// </summary>
internal static class PathNormalizer
{
    /// <summary>
    /// Collapse duplicate slashes, resolve <c>.</c>/<c>..</c> textually, reject <c>\</c>/<c>\0</c>
    /// and root escape. Does not percent-decode.
    /// </summary>
    public static string Canonicalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c is '\\' or '\0')
            {
                throw new InvalidOperationException("Invalid path.");
            }
        }

        // Split on '/' — empty segments from leading/duplicate/trailing slashes are skipped.
        var parts = path.Split('/', StringSplitOptions.None);
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count == 0)
                {
                    throw new InvalidOperationException("Invalid path.");
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(part);
        }

        if (stack.Count == 0)
        {
            return "/";
        }

        return "/" + string.Join('/', stack);
    }
}
