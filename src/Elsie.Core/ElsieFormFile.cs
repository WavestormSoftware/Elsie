namespace Elsie;

/// <summary>Uploaded multipart file part.</summary>
public sealed class ElsieFormFile : IDisposable
{
    private readonly byte[] _bytes;
    private bool _disposed;

    public ElsieFormFile(string name, string? fileName, string? contentType, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);
        Name = name;
        FileName = fileName;
        ContentType = contentType;
        _bytes = bytes;
        Length = bytes.LongLength;
    }

    /// <summary>Form field name.</summary>
    public string Name { get; }

    /// <summary>Client file name, if provided.</summary>
    public string? FileName { get; }

    public string? ContentType { get; }

    public long Length { get; }

    /// <summary>Open a read-only stream over the buffered content.</summary>
    public Stream OpenReadStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new MemoryStream(_bytes, writable: false);
    }

    /// <summary>Copy bytes to a new array.</summary>
    public byte[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bytes.ToArray();
    }

    /// <summary>Raw buffer (do not mutate).</summary>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _bytes;
        }
    }

    public void Dispose() => _disposed = true;
}

/// <summary>Parsed multipart form: text fields + files.</summary>
public sealed class ElsieFormCollection : IDisposable
{
    public ElsieFormCollection(
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        IReadOnlyList<ElsieFormFile> files)
    {
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
        Files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Fields { get; }
    public IReadOnlyList<ElsieFormFile> Files { get; }

    public string? GetField(string name) =>
        Fields.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    public IReadOnlyList<ElsieFormFile> GetFiles(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Files.Where(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public void Dispose()
    {
        foreach (var f in Files)
        {
            f.Dispose();
        }
    }
}
