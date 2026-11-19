using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elsie.Security;

/// <summary>
/// Protects and unprotects small payloads (tokens, cookies, state, ids) with authenticated
/// encryption. Protectors are purpose-bound: data protected for one purpose cannot be unprotected
/// for another.
/// </summary>
public interface IElsieDataProtector
{
    /// <summary>Encrypt+authenticate <paramref name="plaintext"/> (UTF-8) and return Base64Url text.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypt Base64Url payload text. Returns false for tampered, foreign or stale data.</summary>
    bool TryUnprotect(string protectedData, out string? plaintext);

    /// <summary>Encrypt+authenticate raw bytes.</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>Decrypt raw bytes. Returns false for tampered, foreign or stale data.</summary>
    bool TryUnprotect(byte[] protectedData, out byte[]? plaintext);

    /// <summary>A protector bound to an additional purpose string (shares the same key ring).</summary>
    IElsieDataProtector ForPurpose(string purpose);
}

/// <summary>Configuration for <see cref="ElsieDataProtector"/>.</summary>
public sealed class ElsieDataProtectionOptions
{
    /// <summary>
    /// Application name mixed into key derivation so two apps sharing a key ring cannot read each
    /// other's payloads.
    /// </summary>
    public string ApplicationName { get; set; } = "Elsie";

    /// <summary>
    /// Shared 32-byte master key. Use this on every node (and across restarts) so payloads stay
    /// readable. When set, it takes precedence over <see cref="KeyFilePath"/>.
    /// </summary>
    public byte[]? MasterKey { get; set; }

    /// <summary>
    /// Directory file used to persist a generated key ring with rotation. Ignored when
    /// <see cref="MasterKey"/> is set. Created (0600 where supported) when missing.
    /// </summary>
    public string? KeyFilePath { get; set; }

    /// <summary>
    /// Rotate the active key once it is older than this. Retired keys keep decrypting existing
    /// payloads until pruned. Default 90 days.
    /// </summary>
    public TimeSpan KeyLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>Maximum number of retained keys in a persisted ring (oldest pruned first). Default 3.</summary>
    public int MaxKeys { get; set; } = 3;
}

/// <summary>DI registration for <see cref="IElsieDataProtector"/>.</summary>
public static class ElsieDataProtectionServiceCollectionExtensions
{
    /// <summary>
    /// Register the data-protection key ring. Without <see cref="ElsieDataProtectionOptions.MasterKey"/>
    /// or <see cref="ElsieDataProtectionOptions.KeyFilePath"/> an ephemeral process key is used —
    /// fine for tests and local development, not for multiple nodes or restarts.
    /// </summary>
    public static IServiceCollection AddElsieDataProtection(
        this IServiceCollection services,
        Action<ElsieDataProtectionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieDataProtectionOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<IElsieDataProtector>(sp => new ElsieDataProtector(
            sp.GetRequiredService<ElsieDataProtectionOptions>()));
        return services;
    }
}

/// <summary>
/// AES-256-GCM data protector with a versioned, purpose-bound key ring. Envelope:
/// <c>version(1) | keyId(8) | nonce(12) | tag(16) | ciphertext</c>.
/// </summary>
public sealed class ElsieDataProtector : IElsieDataProtector
{
    private const byte EnvelopeVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + 8 + NonceSize;

    private readonly ElsieDataProtectionOptions _options;
    private readonly object _gate = new();
    private readonly string _purpose;
    private List<RingKey> _keys;
    private bool _loadedFromFile;

    public ElsieDataProtector(ElsieDataProtectionOptions options)
        : this(options, purpose: string.Empty)
    {
    }

    private ElsieDataProtector(ElsieDataProtectionOptions options, string purpose)
        : this(options, purpose, keys: null)
    {
    }

    private ElsieDataProtector(ElsieDataProtectionOptions options, string purpose, List<RingKey>? keys)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApplicationName);

        _options = options;
        _purpose = purpose ?? string.Empty;
        _keys = keys ?? LoadKeyRing(options, out _loadedFromFile);
    }

    /// <summary>True when no durable key source was configured (payloads die with the process).</summary>
    public bool IsEphemeral => _options.MasterKey is not { Length: > 0 } && string.IsNullOrWhiteSpace(_options.KeyFilePath);

    /// <inheritdoc />
    public IElsieDataProtector ForPurpose(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        var combined = _purpose.Length == 0 ? purpose : _purpose + ":" + purpose;
        return new ElsieDataProtector(_options, combined, _keys);
    }

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return Base64Url.Encode(Protect(Encoding.UTF8.GetBytes(plaintext)));
    }

    /// <inheritdoc />
    public bool TryUnprotect(string protectedData, out string? plaintext)
    {
        plaintext = null;
        if (string.IsNullOrEmpty(protectedData) || !Base64Url.TryDecode(protectedData, out var payload))
        {
            return false;
        }

        if (!TryUnprotect(payload!, out var bytes))
        {
            return false;
        }

        plaintext = Encoding.UTF8.GetString(bytes!);
        return true;
    }

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var key = CurrentKey();
        var payload = new byte[HeaderSize + TagSize + plaintext.Length];
        payload[0] = EnvelopeVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1, 8), key.Id);

        var nonce = payload.AsSpan(9, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(DeriveKey(key, _purpose), TagSize);
        aes.Encrypt(
            nonce,
            plaintext,
            payload.AsSpan(HeaderSize + TagSize),
            payload.AsSpan(HeaderSize, TagSize),
            payload.AsSpan(0, 1));
        return payload;
    }

    /// <inheritdoc />
    public bool TryUnprotect(byte[] protectedData, out byte[]? plaintext)
    {
        plaintext = null;
        if (protectedData is null || protectedData.Length < HeaderSize + TagSize)
        {
            return false;
        }

        if (protectedData[0] != EnvelopeVersion)
        {
            return false;
        }

        var keyId = BinaryPrimitives.ReadInt64BigEndian(protectedData.AsSpan(1, 8));
        var key = FindKey(keyId);
        if (key is null)
        {
            return false;
        }

        var nonce = protectedData.AsSpan(9, NonceSize);
        var tag = protectedData.AsSpan(HeaderSize, TagSize);
        var ciphertext = protectedData.AsSpan(HeaderSize + TagSize);
        var result = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(DeriveKey(key, _purpose), TagSize);
            aes.Decrypt(nonce, ciphertext, tag, result, protectedData.AsSpan(0, 1));
            plaintext = result;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private byte[] DeriveKey(RingKey key, string purpose)
    {
        var info = Encoding.UTF8.GetBytes($"elsie-dp-v1|{_options.ApplicationName}|{purpose}");
        var salt = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(salt, key.Id);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Material, outputLength: 32, salt: salt, info: info);
    }

    private RingKey CurrentKey()
    {
        lock (_gate)
        {
            RotateIfDue();
            return _keys[^1];
        }
    }

    private RingKey? FindKey(long id)
    {
        lock (_gate)
        {
            foreach (var key in _keys)
            {
                if (key.Id == id)
                {
                    return key;
                }
            }

            return null;
        }
    }

    private void RotateIfDue()
    {
        if (!_loadedFromFile)
        {
            return;
        }

        var newest = _keys[^1];
        if (DateTimeOffset.UtcNow - newest.CreatedAt < _options.KeyLifetime)
        {
            return;
        }

        _keys.Add(NewKey());
        TrimAndPersist();
    }

    private void TrimAndPersist()
    {
        var max = Math.Max(1, _options.MaxKeys);
        while (_keys.Count > max)
        {
            _keys.RemoveAt(0);
        }

        PersistKeyRing(_options.KeyFilePath!, _keys);
    }

    private static List<RingKey> LoadKeyRing(ElsieDataProtectionOptions options, out bool fromFile)
    {
        fromFile = false;

        if (options.MasterKey is { Length: > 0 } master)
        {
            if (master.Length < 16)
            {
                throw new ArgumentException("MasterKey must be at least 16 bytes (32 recommended).", nameof(options));
            }

            return [new RingKey(0, master, DateTimeOffset.UnixEpoch)];
        }

        if (string.IsNullOrWhiteSpace(options.KeyFilePath))
        {
            return [NewKey()];
        }

        fromFile = true;
        var path = options.KeyFilePath;
        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var file = JsonSerializer.Deserialize<KeyFileDocument>(stream);
                if (file?.Keys is { Count: > 0 } entries)
                {
                    var keys = new List<RingKey>(entries.Count);
                    foreach (var entry in entries)
                    {
                        if (entry.Material is { Length: > 0 } material)
                        {
                            keys.Add(new RingKey(entry.Id, material, entry.CreatedAt));
                        }
                    }

                    if (keys.Count > 0)
                    {
                        keys.Sort(static (a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
                        return keys;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Unable to read the Elsie data-protection key ring at '{path}'. " +
                    "Fix the file or point KeyFilePath elsewhere.",
                    ex);
            }
        }

        var created = new List<RingKey> { NewKey() };
        PersistKeyRing(path, created);
        return created;
    }

    private static void PersistKeyRing(string path, List<RingKey> keys)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new KeyFileDocument
        {
            Keys = keys.Select(k => new KeyFileEntry
            {
                Id = k.Id,
                Material = k.Material,
                CreatedAt = k.CreatedAt
            }).ToList()
        };

        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary))
        {
            JsonSerializer.Serialize(stream, document);
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (IOException)
            {
                // Best effort; the move below still succeeds.
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static RingKey NewKey() =>
        new(RandomNumberGenerator.GetInt32(1, int.MaxValue), RandomNumberGenerator.GetBytes(32), DateTimeOffset.UtcNow);

    private sealed record RingKey(long Id, byte[] Material, DateTimeOffset CreatedAt);

    private sealed class KeyFileDocument
    {
        public List<KeyFileEntry> Keys { get; set; } = [];
    }

    private sealed class KeyFileEntry
    {
        public long Id { get; set; }
        public byte[]? Material { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}

/// <summary>Base64Url helper (padding-free, URL-safe); shared by security helpers.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string value, out byte[]? data)
    {
        data = null;
        if (value.Length == 0)
        {
            return false;
        }

        var s = value.Replace('-', '+').Replace('_', '/');
        var remainder = s.Length % 4;
        if (remainder == 1)
        {
            return false;
        }

        if (remainder > 0)
        {
            s += remainder == 2 ? "==" : "=";
        }

        try
        {
            data = Convert.FromBase64String(s);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
