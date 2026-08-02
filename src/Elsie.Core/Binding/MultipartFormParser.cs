using System.Text;

namespace Elsie.Binding;

/// <summary>Minimal multipart/form-data parser for field values and file parts.</summary>
internal static class MultipartFormParser
{
    public const long DefaultMemoryThresholdBytes = 1L * 1024 * 1024;

    public static Dictionary<string, IReadOnlyList<string>> ParseFields(byte[] body, string contentType)
    {
        var parsed = Parse(
            body,
            contentType,
            maxFileBytes: long.MaxValue,
            maxFiles: int.MaxValue,
            memoryThresholdBytes: long.MaxValue);
        // Dispose file buffers — caller only wanted fields
        foreach (var f in parsed.Files)
        {
            f.Dispose();
        }

        return new Dictionary<string, IReadOnlyList<string>>(parsed.Fields, StringComparer.OrdinalIgnoreCase);
    }

    public static ElsieFormCollection Parse(
        byte[] body,
        string contentType,
        long maxFileBytes,
        int maxFiles,
        long memoryThresholdBytes = DefaultMemoryThresholdBytes) =>
        ParseCore(body, contentType, maxFileBytes, maxFiles, memoryThresholdBytes);

    /// <summary>
    /// Read <paramref name="stream"/> to memory (caller should wrap with a size limit) then parse.
    /// Large file parts spill to temp files per <paramref name="memoryThresholdBytes"/>.
    /// </summary>
    public static async Task<ElsieFormCollection> ParseAsync(
        Stream stream,
        string contentType,
        long maxFileBytes,
        int maxFiles,
        long memoryThresholdBytes = DefaultMemoryThresholdBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ParseCore(ms.ToArray(), contentType, maxFileBytes, maxFiles, memoryThresholdBytes);
    }

    private static ElsieFormCollection ParseCore(
        byte[] body,
        string contentType,
        long maxFileBytes,
        int maxFiles,
        long memoryThresholdBytes)
    {
        var boundary = ExtractBoundary(contentType)
            ?? throw new InvalidOperationException("multipart Content-Type is missing boundary.");

        if (memoryThresholdBytes <= 0)
        {
            memoryThresholdBytes = DefaultMemoryThresholdBytes;
        }

        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ElsieFormFile>();
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var closeDelimiter = Encoding.ASCII.GetBytes("--" + boundary + "--");

        var positions = FindBoundaries(body, delimiter, closeDelimiter);
        for (var i = 0; i < positions.Count - 1; i++)
        {
            var start = positions[i] + delimiter.Length;
            if (start + 1 < body.Length && body[start] == (byte)'\r' && body[start + 1] == (byte)'\n')
            {
                start += 2;
            }
            else if (start < body.Length && body[start] == (byte)'\n')
            {
                start += 1;
            }

            var end = positions[i + 1];
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
            string? fileName = null;
            string? partContentType = null;
            foreach (var line in headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                {
                    name = GetDispositionParam(line, "name");
                    fileName = GetDispositionParam(line, "filename");
                }
                else if (line.StartsWith("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    var colon = line.IndexOf(':');
                    if (colon >= 0)
                    {
                        partContentType = line[(colon + 1)..].Trim();
                    }
                }
            }

            if (name is null)
            {
                continue;
            }

            if (fileName is not null)
            {
                if (files.Count >= maxFiles)
                {
                    throw new InvalidOperationException($"Too many uploaded files (max {maxFiles}).");
                }

                if (data.Length > maxFileBytes)
                {
                    throw new InvalidOperationException(
                        $"Uploaded file '{fileName}' exceeds max size of {maxFileBytes} bytes.");
                }

                files.Add(CreateFilePart(name, fileName, partContentType, data, memoryThresholdBytes));
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

        var fields = new Dictionary<string, IReadOnlyList<string>>(map.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in map)
        {
            fields[k] = v;
        }

        return new ElsieFormCollection(fields, files);
    }

    private static ElsieFormFile CreateFilePart(
        string name,
        string fileName,
        string? contentType,
        ReadOnlySpan<byte> data,
        long memoryThresholdBytes)
    {
        if (data.Length <= memoryThresholdBytes)
        {
            return new ElsieFormFile(name, fileName, contentType, data.ToArray());
        }

        var path = Path.Combine(Path.GetTempPath(), "elsie-upload-" + Guid.NewGuid().ToString("n") + ".part");
        try
        {
            using (var fs = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       options: FileOptions.SequentialScan))
            {
                fs.Write(data);
            }

            return ElsieFormFile.FromTempFile(name, fileName, contentType, path, data.Length);
        }
        catch
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }

            throw;
        }
    }

    private static string? ExtractBoundary(string contentType)
    {
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

            if (abs + closeDelimiter.Length <= body.Length &&
                body.AsSpan(abs, closeDelimiter.Length).SequenceEqual(closeDelimiter))
            {
                break;
            }

            i = abs + delimiter.Length;
        }

        if (list.Count > 0)
        {
            var last = list[^1];
            if (!(last + closeDelimiter.Length <= body.Length &&
                  body.AsSpan(last, closeDelimiter.Length).SequenceEqual(closeDelimiter)))
            {
                var closeAt = IndexOf(body.AsSpan(), closeDelimiter);
                if (closeAt >= 0 && !list.Contains(closeAt))
                {
                    list.Add(closeAt);
                }
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
