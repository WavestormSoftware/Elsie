namespace Elsie.Cors;

/// <summary>
/// Config-bindable shape of <see cref="ElsieCorsOptions"/> (section <c>Elsie:Cors</c>). Policies are
/// rebuilt from here when the section reloads; programmatic <see cref="ElsieCorsOptions.AddPolicy"/>
/// configuration remains authoritative when no <c>Elsie:Cors</c> section is present.
/// </summary>
public sealed class ElsieCorsConfigurationOptions
{
    public string DefaultPolicy { get; set; } = ElsieCorsOptions.DefaultPolicyName;

    public Dictionary<string, ElsieCorsPolicyConfiguration> Policies { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Config-bindable CORS policy definition.</summary>
public sealed class ElsieCorsPolicyConfiguration
{
    public bool AllowAnyOrigin { get; set; }
    public bool AllowAnyMethod { get; set; }
    public bool AllowAnyHeader { get; set; }
    public string[] Origins { get; set; } = [];
    public string[] Methods { get; set; } = [];
    public string[] Headers { get; set; } = [];
    public string[] ExposedHeaders { get; set; } = [];
    public bool AllowCredentials { get; set; }
    public TimeSpan? PreflightMaxAge { get; set; }
}
