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
        return "v1." + Base64UrlEncode(packed);
    }

    public static bool TryUnprotect(string token, byte[] key, out ClaimsPrincipal? principal, out DateTimeOffset expiresUtc)
    {
        principal = null;
        expiresUtc = default;
        if (string.IsNullOrEmpty(token) || !token.StartsWith("v1.", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] packed;
        try
        {
            packed = Base64UrlDecode(token[3..]);
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

        var claims = payload.Claims?.Select(c => new Claim(c.Type, c.Value)) ?? Array.Empty<Claim>();
        var identity = new ClaimsIdentity(claims, payload.AuthType ?? "Cookies");
        principal = new ClaimsPrincipal(identity);
        return true;
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
