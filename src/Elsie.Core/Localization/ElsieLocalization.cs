using System.Globalization;
using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Localization;

/// <summary>Options for request culture resolution and string lookup.</summary>
public sealed class ElsieLocalizationOptions
{
    /// <summary>Fallback culture when nothing matches. Default <c>en</c>.</summary>
    public string DefaultCulture { get; set; } = "en";

    /// <summary>Cultures the app serves. Empty means any requested culture is accepted.</summary>
    public List<string> SupportedCultures { get; } = [];

    /// <summary>Query parameter that overrides the requested culture. Default <c>culture</c>.</summary>
    public string QueryParameterName { get; set; } = "culture";

    /// <summary>Use <c>Accept-Language</c> when no query override is present. Default true.</summary>
    public bool RespectAcceptLanguage { get; set; } = true;

    /// <summary>Translations keyed by culture then message key.</summary>
    public Dictionary<string, Dictionary<string, string>> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Resolves the request culture and exposes translations.</summary>
public static class ElsieLocalization
{
    private static readonly object CultureKey = new();

    /// <summary>Culture resolved for this request (falls back to the configured default).</summary>
    public static CultureInfo GetCulture(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.Items.TryGetValue(CultureKey, out var value) && value is CultureInfo resolved)
        {
            return resolved;
        }

        var fallback = context.GetService<ElsieLocalizationOptions>()?.DefaultCulture ?? "en";
        return CultureInfo.GetCultureInfo(fallback);
    }

    /// <summary>Override the culture for the rest of the request.</summary>
    public static void SetCulture(this ElsieContext context, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(culture);
        context.Request.Items[CultureKey] = culture;
    }

    /// <summary>
    /// Translate <paramref name="key"/> for the request culture, falling back to the default
    /// culture and then to the key itself.
    /// </summary>
    public static string T(this ElsieContext context, string key)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var options = context.GetService<ElsieLocalizationOptions>();
        if (options is null)
        {
            return key;
        }

        var culture = context.GetCulture();
        foreach (var candidate in Candidates(culture, options.DefaultCulture))
        {
            if (options.Resources.TryGetValue(candidate, out var table)
                && table.TryGetValue(key, out var translated))
            {
                return translated;
            }
        }

        return key;
    }

    private static IEnumerable<string> Candidates(CultureInfo culture, string fallback)
    {
        if (!string.IsNullOrEmpty(culture.Name))
        {
            yield return culture.Name;
            if (culture.Parent is { Name.Length: > 0 } parent)
            {
                yield return parent.Name;
            }
        }

        yield return fallback;
    }
}

/// <summary>
/// Resolves the request culture from the query override or <c>Accept-Language</c>, honours the
/// supported-culture list, and advertises the result via <c>Content-Language</c> / <c>Vary</c>.
/// </summary>
public sealed class ElsieLocalizationMiddleware : IElsieMiddleware
{
    private readonly ElsieLocalizationOptions _options;

    public ElsieLocalizationMiddleware(ElsieLocalizationOptions? options = null)
    {
        _options = options ?? new ElsieLocalizationOptions();
    }

    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var culture = Resolve(context);
        context.SetCulture(culture);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        await next(context).ConfigureAwait(false);

        context.Result = context.Result is { } result
            ? result
                .WithHeader("Content-Language", culture.Name)
                .WithHeader("Vary", "Accept-Language")
            : context.Result;
    }

    private CultureInfo Resolve(ElsieContext context)
    {
        var requested = context.Request.GetQuery(_options.QueryParameterName);
        if (string.IsNullOrWhiteSpace(requested) && _options.RespectAcceptLanguage)
        {
            requested = PickFromAcceptLanguage(context.Request.GetHeader("Accept-Language"));
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var name = requested.Split(',', ';', '-')[0].Trim();
            if (IsSupported(requested.Trim(), name))
            {
                try
                {
                    return CultureInfo.GetCultureInfo(requested.Trim());
                }
                catch (CultureNotFoundException)
                {
                    // fall through to default
                }
            }
        }

        return CultureInfo.GetCultureInfo(_options.DefaultCulture);
    }

    private bool IsSupported(string full, string neutral)
    {
        if (_options.SupportedCultures.Count == 0)
        {
            return true;
        }

        return _options.SupportedCultures.Any(c =>
            string.Equals(c, full, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c, neutral, StringComparison.OrdinalIgnoreCase));
    }

    private static string? PickFromAcceptLanguage(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        // Highest q-value wins; ties keep client order.
        string? best = null;
        var bestQuality = -1.0;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(';', StringSplitOptions.TrimEntries);
            var tag = pieces[0];
            var quality = 1.0;
            foreach (var piece in pieces.Skip(1))
            {
                if (piece.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(piece[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                {
                    quality = parsed;
                }
            }

            if (quality > bestQuality)
            {
                bestQuality = quality;
                best = tag;
            }
        }

        return best;
    }
}

/// <summary>Registration for localization.</summary>
public static class ElsieLocalizationExtensions
{
    public static IServiceCollection AddElsieLocalization(
        this IServiceCollection services,
        Action<ElsieLocalizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ElsieLocalizationOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton(new ElsieMiddlewareSetup(p => p.Use(new ElsieLocalizationMiddleware(options))));
        return services;
    }
}
