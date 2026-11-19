using System.Text;
using Elsie.Security;
using Xunit;

namespace Elsie.Tests;

public class DataProtectionTests
{
    private static byte[] MasterKey() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Protect_roundtrips_string_and_bytes()
    {
        var protector = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey() });

        var payload = protector.Protect("hello");
        Assert.True(protector.TryUnprotect(payload, out var plaintext));
        Assert.Equal("hello", plaintext);

        var bytes = protector.Protect(Encoding.UTF8.GetBytes("raw"));
        Assert.True(protector.TryUnprotect(bytes, out var raw));
        Assert.Equal("raw", Encoding.UTF8.GetString(raw!));
    }

    [Fact]
    public void Protection_is_purpose_bound()
    {
        var protector = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey() });
        var payload = protector.ForPurpose("email-confirmation").Protect("user-1");

        Assert.True(protector.ForPurpose("email-confirmation").TryUnprotect(payload, out var value));
        Assert.Equal("user-1", value);

        Assert.False(protector.ForPurpose("password-reset").TryUnprotect(payload, out _));
        Assert.False(protector.TryUnprotect(payload, out _));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var protector = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey() });
        var payload = protector.Protect(Encoding.UTF8.GetBytes("secret"));
        payload[^1] ^= 0xFF;

        Assert.False(protector.TryUnprotect(payload, out _));
    }

    [Fact]
    public void Shared_master_key_interoperates_across_instances()
    {
        var a = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey() });
        var b = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey() });

        var payload = a.ForPurpose("state").Protect("abc");
        Assert.True(b.ForPurpose("state").TryUnprotect(payload, out var value));
        Assert.Equal("abc", value);
    }

    [Fact]
    public void Different_application_names_do_not_interoperate()
    {
        var a = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey(), ApplicationName = "app-a" });
        var b = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey(), ApplicationName = "app-b" });

        Assert.False(b.TryUnprotect(a.Protect("x"), out _));
    }

    [Fact]
    public void Key_file_persists_across_instances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "elsie-dp-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "keyring.json");
        try
        {
            var first = new ElsieDataProtector(new ElsieDataProtectionOptions { KeyFilePath = path });
            Assert.True(File.Exists(path));
            var payload = first.Protect("durable");

            var second = new ElsieDataProtector(new ElsieDataProtectionOptions { KeyFilePath = path });
            Assert.True(second.TryUnprotect(payload, out var value));
            Assert.Equal("durable", value);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Long_lived_payload_survives_key_rotation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "elsie-dp-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "keyring.json");
        try
        {
            var first = new ElsieDataProtector(new ElsieDataProtectionOptions
            {
                KeyFilePath = path,
                KeyLifetime = TimeSpan.FromDays(1)
            });
            var payload = first.Protect("old");

            // Advance the ring by rotating on a second instance with a zero lifetime.
            var rotating = new ElsieDataProtector(new ElsieDataProtectionOptions
            {
                KeyFilePath = path,
                KeyLifetime = TimeSpan.Zero
            });
            _ = rotating.Protect("new");
            _ = rotating.Protect("newer");

            var reopened = new ElsieDataProtector(new ElsieDataProtectionOptions { KeyFilePath = path });
            Assert.True(reopened.TryUnprotect(payload, out var value));
            Assert.Equal("old", value);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Ephemeral_protector_reports_no_durable_key()
    {
        var protector = new ElsieDataProtector(new ElsieDataProtectionOptions());
        Assert.True(protector.IsEphemeral);
        Assert.True(protector.TryUnprotect(protector.Protect("in-memory"), out var value));
        Assert.Equal("in-memory", value);
    }

    [Fact]
    public void Invalid_input_is_reported_as_failure()
    {
        var protector = new ElsieDataProtector(new ElsieDataProtectionOptions { MasterKey = MasterKey() });
        Assert.False(protector.TryUnprotect("not-base64!!", out _));
        Assert.False(protector.TryUnprotect(string.Empty, out _));
        Assert.False(protector.TryUnprotect(new byte[] { 1, 2, 3 }, out _));
    }
}
