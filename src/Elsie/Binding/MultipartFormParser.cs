using System.Text;

namespace Elsie.Binding;

/// <summary>Minimal multipart/form-data parser for field values (files returned as raw parts optionally).</summary>
internal static class MultipartFormParser
{
    public static Dictionary<string, IReadOnlyList<string>> ParseFields(byte[] body, string contentType)
    {
        var boundary = ExtractBoundary(contentType)
            ?? throw new InvalidOperationException("multipart Content-Type is missing boundary.");

        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var closeDelimiter = Encoding.ASCII.GetBytes("--" + boundary + "--");

        var positions = FindBoundaries(body, delimiter, closeDelimiter);
        for (var i = 0; i < positions.Count - 1; i++)
        {
            var start = positions[i] + delimiter.Length;
            // skip leading CRLF after boundary
            if (start + 1 < body.Length && body[start] == (byte)'\r' && body[start + 1] == (byte)'\n')
            {
                start += 2;
            }
            else if (start < body.Length && body[start] == (byte)'\n')
            {
                start += 1;
            }

            var end = positions[i + 1];
            // strip trailing CRLF before next boundary
            if (end >= 2 && body[end - 2] == (byte)'\r' && body[end - 1] == (byte)'\n')
            {
                end -= 2;
            }
            else if (end >= 1 && body[end - 1] == (byte)'\n')
            {
                end -= 1;
            }

            if (end <= start)
            {
                continue;
            }

            var part = body.AsSpan(start, end - start);
            var headerEnd = IndexOf(part, "\r\n\r\n"u8);
            var headerSepLen = 4;
            if (headerEnd < 0)
            {
                headerEnd = IndexOf(part, "\n\n"u8);
                headerSepLen = 2;
            }

            if (headerEnd < 0)
            {
                continue;
            }

            var headerText = Encoding.UTF8.GetString(part[..headerEnd]);
            var data = part[(headerEnd + headerSepLen)..];

            string? name = null;
            var isFile = false;
            foreach (var line in headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                name = GetDispositionParam(line, "name");
                if (GetDispositionParam(line, "filename") is not null)
                {
                    isFile = true;
                }
            }

            if (name is null || isFile)
            {
                // Skip file parts for POCO field binding.
                continue;
            }

            var value = Encoding.UTF8.GetString(data);
            if (!map.TryGetValue(name, out var list))
            {
                list = new List<string>(1);
                map[name] = list;
            }

            list.Add(value);
        }

        var result = new Dictionary<string, IReadOnlyList<string>>(map.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in map)
        {
            result[k] = v;
        }

        return result;
    }

    private static string? ExtractBoundary(string contentType)
    {
        // Content-Type: multipart/form-data; boundary=----WebKit...
        foreach (var part in contentType.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                var b = part["boundary=".Length..].Trim();
                if (b.Length >= 2 && b[0] == '"' && b[^1] == '"')
                {
                    b = b[1..^1];
                }

                return b;
            }
        }

        return null;
    }

    private static List<int> FindBoundaries(byte[] body, byte[] delimiter, byte[] closeDelimiter)
    {
        var list = new List<int>();
        var i = 0;
        while (i < body.Length)
        {
            var at = IndexOf(body.AsSpan(i), delimiter);
            if (at < 0)
            {
                break;
            }

            var abs = i + at;
            list.Add(abs);

            // If this is the close delimiter, stop after recording
            if (abs + closeDelimiter.Length <= body.Length &&
                body.AsSpan(abs, closeDelimiter.Length).SequenceEqual(closeDelimiter))
            {
                break;
            }

            i = abs + delimiter.Length;
        }

        // Also mark end-of-body as final position if last was not close
        if (list.Count > 0)
        {
            var last = list[^1];
            if (!(last + closeDelimiter.Length <= body.Length &&
                  body.AsSpan(last, closeDelimiter.Length).SequenceEqual(closeDelimiter)))
            {
                // Find close
                var closeAt = IndexOf(body.AsSpan(), closeDelimiter);
                if (closeAt >= 0 && !list.Contains(closeAt))
                {
                    list.Add(closeAt);
                }
            }
            else if (list.Count == 1)
            {
                // only close found — no parts
            }
        }

        return list;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    private static string? GetDispositionParam(string headerLine, string param)
    {
        // name="field" or name=field
        var key = param + "=";
        var idx = headerLine.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var rest = headerLine[(idx + key.Length)..].Trim();
        if (rest.Length == 0)
        {
            return null;
        }

        if (rest[0] == '"')
        {
            var end = rest.IndexOf('"', 1);
            return end < 0 ? rest[1..] : rest[1..end];
        }

        var semi = rest.IndexOf(';');
        return semi < 0 ? rest : rest[..semi].Trim();
    }
}
