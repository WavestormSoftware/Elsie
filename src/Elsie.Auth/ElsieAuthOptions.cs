using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Elsie.Auth;

/// <summary>Cookie and/or JWT bearer setup for <c>AddElsieAuth</c>.</summary>
public sealed class ElsieAuthOptions
{
    /// <summary>Default authenticate/challenge scheme. Auto-picked when only one scheme is configured.</summary>
    public string? DefaultScheme { get; set; }

    public string CookieScheme { get; set; } = CookieAuthenticationDefaults.AuthenticationScheme;

    public string JwtBearerScheme { get; set; } = JwtBearerDefaults.AuthenticationScheme;

    /// <summary>When set, registers cookie authentication via this configure callback.</summary>
    public Action<CookieAuthenticationOptions>? Cookie { get; set; }

    /// <summary>When set, registers JWT bearer authentication via this configure callback.</summary>
    public Action<JwtBearerOptions>? JwtBearer { get; set; }

    /// <summary>Optional authorization options (policies, etc.).</summary>
    public Action<AuthorizationOptions>? Authorization { get; set; }
}
