namespace Elsie.Cors;

/// <summary>Named CORS policies. Default policy name is <see cref="DefaultPolicyName"/>.</summary>
public sealed class ElsieCorsOptions
{
    public const string DefaultPolicyName = "Default";

    private readonly Dictionary<string, ElsieCorsPolicy> _policies =
        new(StringComparer.Ordinal);

    public string DefaultPolicy { get; set; } = DefaultPolicyName;

    public ElsieCorsOptions AddDefaultPolicy(Action<ElsieCorsPolicy> configure) =>
        AddPolicy(DefaultPolicyName, configure);

    public ElsieCorsOptions AddPolicy(string name, Action<ElsieCorsPolicy> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var policy = new ElsieCorsPolicy();
        configure(policy);
        if (policy.AllowAnyOrigin && policy.SupportsCredentials)
        {
            throw new InvalidOperationException(
                "CORS policy cannot allow any origin ('*') when credentials are enabled.");
        }

        _policies[name] = policy;
        return this;
    }

    public bool TryGetPolicy(string name, out ElsieCorsPolicy policy) =>
        _policies.TryGetValue(name, out policy!);

    public ElsieCorsPolicy GetRequiredPolicy(string name)
    {
        if (TryGetPolicy(name, out var policy))
        {
            return policy;
        }

        throw new InvalidOperationException($"CORS policy '{name}' is not registered.");
    }
}
