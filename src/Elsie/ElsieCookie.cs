namespace Elsie;

/// <summary>SameSite attribute for <c>Set-Cookie</c>.</summary>
public enum ElsieSameSite
{
    Unspecified = 0,
    None = 1,
    Lax = 2,
    Strict = 3
}

/// <summary>Options for <see cref="ElsieResponse.SetCookie"/>.</summary>
public sealed class ElsieCookieOptions
{
    public string? Path { get; set; } = "/";
    public string? Domain { get; set; }
    public bool HttpOnly { get; set; }
    public bool Secure { get; set; }
    public ElsieSameSite SameSite { get; set; } = ElsieSameSite.Unspecified;
    public DateTimeOffset? Expires { get; set; }
    public TimeSpan? MaxAge { get; set; }
}

internal static class ElsieCookieFormatter
{
    public static string FormatSetCookie(string name, string value, ElsieCookieOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        options ??= new ElsieCookieOptions();

        // name=value; Path=/; ...
        var sb = new System.Text.StringBuilder();
        sb.Append(Uri.EscapeDataString(name));
        sb.Append('=');
        sb.Append(Uri.EscapeDataString(value));

        if (!string.IsNullOrEmpty(options.Path))
        {
            sb.Append("; Path=").Append(options.Path);
        }

        if (!string.IsNullOrEmpty(options.Domain))
        {
            sb.Append("; Domain=").Append(options.Domain);
        }

        if (options.Expires is { } expires)
        {
            sb.Append("; Expires=").Append(expires.UtcDateTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (options.MaxAge is { } maxAge)
        {
            sb.Append("; Max-Age=").Append(((long)maxAge.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (options.HttpOnly)
        {
            sb.Append("; HttpOnly");
        }

        if (options.Secure)
        {
            sb.Append("; Secure");
        }

        switch (options.SameSite)
        {
            case ElsieSameSite.None:
                sb.Append("; SameSite=None");
                break;
            case ElsieSameSite.Lax:
                sb.Append("; SameSite=Lax");
                break;
            case ElsieSameSite.Strict:
                sb.Append("; SameSite=Strict");
                break;
        }

        return sb.ToString();
    }

    public static string FormatDeleteCookie(string name, ElsieCookieOptions? options)
    {
        options ??= new ElsieCookieOptions();
        // Deletion: empty value + past Expires + Max-Age=0
        var delete = new ElsieCookieOptions
        {
            Path = options.Path ?? "/",
            Domain = options.Domain,
            Secure = options.Secure,
            HttpOnly = options.HttpOnly,
            SameSite = options.SameSite,
            Expires = DateTimeOffset.UnixEpoch,
            MaxAge = TimeSpan.Zero
        };
        return FormatSetCookie(name, string.Empty, delete);
    }
}
