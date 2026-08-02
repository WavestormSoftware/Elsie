using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Elsie.Auth;

internal static class CookieTicketProtector
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>v1 ticket prefix (client-side encrypted principal).</summary>
    public const string V1Prefix = "v1.";

    /// <summary>v2 ticket prefix (opaque server-side session id).</summary>
    public const string V2Prefix = "v2.";

    public static string Protect(ClaimsPrincipal principal, DateTimeOffset expiresUtc, byte[] key)
    {
        var payload = new TicketPayload
        {
            Exp = expiresUtc.ToUnixTimeSeconds(),
            Claims = principal.Claims.Select(c => new TicketClaim(c.Type, c.Value)).ToArray(),
            AuthType = principal.Identity?.AuthenticationType ?? "Cookies"
        };

        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(NormalizeKey(key), tag.Length);
        aes.Encrypt(nonce, plain, cipher, tag);

        // format: v1.base64url(nonce|tag|cipher)
        var packed = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, packed, nonce.Length + tag.Length, cipher.Length);
        return V1Prefix + Base64UrlEncode(packed);
    }

    public static bool TryUnprotect(string token, byte[] key, out ClaimsPrincipal? principal, out DateTimeOffset expiresUtc)
    {
        principal = null;
        expiresUtc = default;
        if (string.IsNullOrEmpty(token) || !token.StartsWith(V1Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        byte[] packed;
        try
        {
            packed = Base64UrlDecode(token[V1Prefix.Length..]);
        }
        catch
        {
            return false;
        }

        if (packed.Length < 12 + 16 + 1)
        {
            return false;
        }

        var nonce = packed.AsSpan(0, 12);
        var tag = packed.AsSpan(12, 16);
        var cipher = packed.AsSpan(28);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(NormalizeKey(key), 16);
            aes.Decrypt(nonce, cipher, tag, plain);
        }
        catch (CryptographicException)
        {
            return false;
        }

        TicketPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TicketPayload>(plain, JsonOptions);
        }
        catch
        {
            return false;
        }

        if (payload is null)
        {
            return false;
        }

        expiresUtc = DateTimeOffset.FromUnixTimeSeconds(payload.Exp);
        if (expiresUtc <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        principal = ToPrincipal(payload);
        return true;
    }

    /// <summary>Builds an opaque v2 session id cookie value from a ≥16-byte session id.</summary>
    public static string ProtectServerSideSession(byte[] sessionId)
    {
        if (sessionId.Length < 16)
        {
            throw new ArgumentException("Server-side session ids must be at least 16 bytes (128-bit).", nameof(sessionId));
        }

        return V2Prefix + Base64UrlEncode(sessionId);
    }

    /// <summary>True when the cookie value is a v2 opaque session id.</summary>
    public static bool IsVersion2(string token) =>
        !string.IsNullOrEmpty(token) && token.StartsWith(V2Prefix, StringComparison.Ordinal);

    /// <summary>Extracts the opaque session id from a v2 cookie value.</summary>
    public static bool TryGetSessionId(string token, out byte[] sessionId)
    {
        sessionId = [];
        if (!IsVersion2(token))
        {
            return false;
        }

        try
        {
            sessionId = Base64UrlDecode(token[V2Prefix.Length..]);
        }
        catch
        {
            return false;
        }

        return sessionId.Length >= 16;
    }

    /// <summary>Serializes a principal for storage in a session store.</summary>
    public static byte[] SerializePrincipal(ClaimsPrincipal principal)
    {
        var payload = new TicketPayload
        {
            Exp = DateTimeOffset.MaxValue.ToUnixTimeSeconds(),
            Claims = principal.Claims.Select(c => new TicketClaim(c.Type, c.Value)).ToArray(),
            AuthType = principal.Identity?.AuthenticationType ?? "Cookies"
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
    }

    /// <summary>Deserializes a principal from <see cref="SerializePrincipal"/>; null when invalid.</summary>
    public static ClaimsPrincipal? TryDeserializePrincipal(byte[] data)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<TicketPayload>(data, JsonOptions);
            return payload is null ? null : ToPrincipal(payload);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Generates a new opaque session id (128-bit random).</summary>
    public static byte[] NewSessionId() => RandomNumberGenerator.GetBytes(16);

    /// <summary>Encodes a raw session id for use as a store key (URL-safe).</summary>
    public static string ToSessionIdString(byte[] sessionId) => Base64UrlEncode(sessionId);

    private static ClaimsPrincipal ToPrincipal(TicketPayload payload)
    {
        var claims = payload.Claims?.Select(c => new Claim(c.Type, c.Value)) ?? Array.Empty<Claim>();
        var identity = new ClaimsIdentity(claims, payload.AuthType ?? "Cookies");
        return new ClaimsPrincipal(identity);
    }

    private static byte[] NormalizeKey(byte[] key)
    {
        if (key.Length == 32)
        {
            return key;
        }

        return SHA256.HashData(key);
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }

    private sealed class TicketPayload
    {
        public long Exp { get; set; }
        public TicketClaim[]? Claims { get; set; }
        public string? AuthType { get; set; }
    }

    private sealed record TicketClaim(string Type, string Value);
}
