namespace Elsie;

/// <summary>Uploaded multipart file part (memory- or temp-file-backed).</summary>
public sealed class ElsieFormFile : IDisposable, IAsyncDisposable
{
    private byte[]? _bytes;
    private string? _tempPath;
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

    private ElsieFormFile(string name, string? fileName, string? contentType, string tempPath, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempPath);
        Name = name;
        FileName = fileName;
        ContentType = contentType;
        _tempPath = tempPath;
        Length = length;
    }

    /// <summary>Create a file part backed by a temp path (deleted on dispose).</summary>
    internal static ElsieFormFile FromTempFile(
        string name,
        string? fileName,
        string? contentType,
        string tempPath,
        long length) =>
        new(name, fileName, contentType, tempPath, length);

    /// <summary>Form field name.</summary>
    public string Name { get; }

    /// <summary>Client file name, if provided.</summary>
    public string? FileName { get; }

    public string? ContentType { get; }

    public long Length { get; }

    /// <summary>True when content lives in a temp file rather than a managed byte array.</summary>
    public bool IsFileBacked
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _tempPath is not null;
        }
    }

    /// <summary>Temp path when <see cref="IsFileBacked"/>; otherwise null. For tests/diagnostics.</summary>
    internal string? TempPath => _tempPath;

    /// <summary>Open a read-only stream over the content.</summary>
    public Stream OpenReadStream()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bytes is not null)
        {
            return new MemoryStream(_bytes, writable: false);
        }

        return new FileStream(
            _tempPath!,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    /// <summary>Copy bytes to a new array (loads temp file into memory when file-backed).</summary>
    public byte[] ToArray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bytes is not null)
        {
            return _bytes.ToArray();
        }

        return File.ReadAllBytes(_tempPath!);
    }

    /// <summary>Raw buffer when memory-backed (do not mutate). File-backed loads into a new array.</summary>
    public ReadOnlyMemory<byte> Memory
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bytes is not null)
            {
                return _bytes;
            }

            return File.ReadAllBytes(_tempPath!);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _bytes = null;
        DeleteTemp();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void DeleteTemp()
    {
        var path = Interlocked.Exchange(ref _tempPath, null);
        if (path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}

/// <summary>Parsed multipart form: text fields + files.</summary>
public sealed class ElsieFormCollection : IDisposable, IAsyncDisposable
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

    public async ValueTask DisposeAsync()
    {
        foreach (var f in Files)
        {
            await f.DisposeAsync().ConfigureAwait(false);
        }
    }
}
