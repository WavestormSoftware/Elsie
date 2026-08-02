using Microsoft.Extensions.Options;

namespace Elsie.Cors;

/// <summary>
/// Applies <c>Elsie:Cors</c> config-section reloads onto the live <see cref="ElsieCorsOptions"/>
/// singleton so CORS origins/methods/headers hot reload without restart. When the config section
/// defines no policies, programmatic configuration stays authoritative.
/// </summary>
internal sealed class ElsieCorsConfigRelay
{
    public ElsieCorsConfigRelay(
        ElsieCorsOptions live,
        IOptionsMonitor<ElsieCorsConfigurationOptions> monitor)
    {
        Apply(monitor.CurrentValue, live);
        monitor.OnChange(config => Apply(config, live));
    }

    private static void Apply(ElsieCorsConfigurationOptions config, ElsieCorsOptions live)
    {
        if (config.Policies.Count == 0)
        {
            return;
        }

        var rebuilt = new ElsieCorsOptions { DefaultPolicy = config.DefaultPolicy };
        foreach (var (name, policyConfig) in config.Policies)
        {
            rebuilt.AddPolicy(name, p => p.ApplyConfig(policyConfig));
        }

        live.CopyFrom(rebuilt);
    }
}
