namespace Elsie.Extensions.Auth.Redis;

/// <summary>
/// Configuration for <see cref="RedisSessionStore"/>.
/// </summary>
public sealed class RedisSessionStoreOptions
{
    /// <summary>Key prefix for session entries. Default <c>elsie:session:</c>.</summary>
    public string KeyPrefix { get; set; } = "elsie:session:";

    /// <summary>Per-operation timeout in milliseconds (default 100). On timeout the operation fails.</summary>
    public int OperationTimeoutMilliseconds { get; set; } = 100;

    /// <summary>Validates and returns the normalized key prefix (must end with ':').</summary>
    internal string NormalizedPrefix()
    {
        var prefix = string.IsNullOrWhiteSpace(KeyPrefix) ? "elsie:session:" : KeyPrefix;
        return prefix.EndsWith(':')
            ? prefix
            : prefix + ":";
    }
}
