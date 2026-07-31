using System.Security.Claims;
using System.Text;
using Elsie.Auth;
using Xunit;

namespace Elsie.Auth.Tests;

public class CookieTicketSecurityTests
{
    [Fact]
    public void Roundtrip_principal()
    {
        var key = SHA("ticket-secret-key-01");
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "ada"), new Claim(ClaimTypes.Role, "admin") },
            "Cookies");
        var principal = new ClaimsPrincipal(identity);
        var exp = DateTimeOffset.UtcNow.AddHours(1);
        var token = CookieTicketProtector.Protect(principal, exp, key);
        Assert.StartsWith("v1.", token, StringComparison.Ordinal);

        Assert.True(CookieTicketProtector.TryUnprotect(token, key, out var restored, out var gotExp));
        Assert.NotNull(restored);
        Assert.Equal("ada", restored!.Identity!.Name);
        Assert.True(restored.IsInRole("admin"));
        Assert.True(gotExp > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Tampered_ciphertext_fails()
    {
        var key = SHA("ticket-secret-key-01");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "ada") }, "Cookies"));
        var token = CookieTicketProtector.Protect(principal, DateTimeOffset.UtcNow.AddHours(1), key);
        var chars = token.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        Assert.False(CookieTicketProtector.TryUnprotect(tampered, key, out _, out _));
    }

    [Fact]
    public void Wrong_key_fails()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "ada") }, "Cookies"));
        var token = CookieTicketProtector.Protect(principal, DateTimeOffset.UtcNow.AddHours(1), SHA("key-aaaaaaaaaaaaaa"));
        Assert.False(CookieTicketProtector.TryUnprotect(token, SHA("key-bbbbbbbbbbbbbb"), out _, out _));
    }

    [Fact]
    public void Expired_ticket_fails()
    {
        var key = SHA("ticket-secret-key-01");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "ada") }, "Cookies"));
        var token = CookieTicketProtector.Protect(principal, DateTimeOffset.UtcNow.AddSeconds(-5), key);
        Assert.False(CookieTicketProtector.TryUnprotect(token, key, out _, out _));
    }

    [Fact]
    public void Empty_or_garbage_fails()
    {
        var key = SHA("ticket-secret-key-01");
        Assert.False(CookieTicketProtector.TryUnprotect("", key, out _, out _));
        Assert.False(CookieTicketProtector.TryUnprotect("not-v1", key, out _, out _));
        Assert.False(CookieTicketProtector.TryUnprotect("v1.!!!", key, out _, out _));
    }

    private static byte[] SHA(string s) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s));
}
