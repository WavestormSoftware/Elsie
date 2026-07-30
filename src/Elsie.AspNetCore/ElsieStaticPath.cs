namespace Elsie.AspNetCore;

/// <summary>Safe static-file path resolution (testable).</summary>
internal static class ElsieStaticPath
{
    public static bool TryResolve(string rootFull, string relative, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrEmpty(relative))
        {
            return false;
        }

        if (relative.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        // Normalize separators; reject absolute / rooted inputs.
        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            Path.IsPathRooted(relative) ||
            normalized.Contains(":/", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "..")
            {
                return false;
            }
        }

        string combined;
        try
        {
            combined = Path.GetFullPath(
                Path.Combine(rootFull, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = combined;
        return true;
    }

    public static bool IsUnderMount(string path, string mount, out string relative)
    {
        relative = string.Empty;
        if (mount == "/")
        {
            relative = path.TrimStart('/');
            return true;
        }

        if (!path.StartsWith(mount, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.Length == mount.Length)
        {
            relative = string.Empty;
            return true;
        }

        if (path[mount.Length] != '/')
        {
            return false;
        }

        relative = path[(mount.Length + 1)..];
        return true;
    }

    public static string NormalizeMount(string requestPath)
    {
        var p = requestPath.Trim();
        if (p.Length == 0 || p == "/")
        {
            return "/";
        }

        if (p[0] != '/')
        {
            p = "/" + p;
        }

        return p.TrimEnd('/');
    }
}
