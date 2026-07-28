using System.Net;
using System.Text.RegularExpressions;

namespace Elsie.Views;

/// <summary>
/// Tiny file template engine: <c>{{Path}}</c>, <c>{{{raw}}}</c>, optional <c>@layout Name</c> + <c>{{body}}</c>.
/// </summary>
public sealed class ElsieFileViewEngine : IElsieViewEngine
{
    private static readonly Regex TokenRegex = new(
        @"\{\{\{(?<raw>[^}]+)\}\}\}|\{\{(?<enc>[^}]+)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ElsieViewOptions _options;

    public ElsieFileViewEngine(ElsieViewOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string> RenderAsync(string viewName, object? model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        var viewPath = ResolvePath(viewName);
        var raw = await File.ReadAllTextAsync(viewPath, cancellationToken).ConfigureAwait(false);
        var (layoutName, bodyTemplate) = SplitLayoutDirective(raw);
        var body = ReplaceTokens(bodyTemplate, model);

        if (layoutName is null)
        {
            return body;
        }

        var layoutPath = ResolvePath(layoutName);
        var layout = await File.ReadAllTextAsync(layoutPath, cancellationToken).ConfigureAwait(false);
        // Keep rendered body out of HTML-encoding while still allowing {{Model}} tokens in the layout.
        const string bodyMarker = "ELSIE_BODY";
        var staged = layout.Replace("{{body}}", bodyMarker, StringComparison.Ordinal);
        return ReplaceTokens(staged, model).Replace(bodyMarker, body, StringComparison.Ordinal);
    }

    private string ResolvePath(string name)
    {
        if (name.Contains("..", StringComparison.Ordinal) ||
            name.Contains(':', StringComparison.Ordinal) ||
            Path.IsPathRooted(name))
        {
            throw new InvalidOperationException($"View name '{name}' is invalid.");
        }

        var relative = name.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        if (!relative.EndsWith(_options.Extension, StringComparison.OrdinalIgnoreCase))
        {
            relative += _options.Extension;
        }

        var full = Path.GetFullPath(Path.Combine(_options.ContentRoot, _options.RootPath, relative));
        var root = Path.GetFullPath(Path.Combine(_options.ContentRoot, _options.RootPath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"View name '{name}' escapes the views root.");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"View '{name}' was not found at '{full}'.", full);
        }

        return full;
    }

    private static (string? LayoutName, string Template) SplitLayoutDirective(string content)
    {
        using var reader = new StringReader(content);
        var first = reader.ReadLine();
        if (first is null)
        {
            return (null, content);
        }

        var trimmed = first.Trim();
        if (trimmed.StartsWith("@layout ", StringComparison.Ordinal))
        {
            var layout = trimmed["@layout ".Length..].Trim();
            var rest = reader.ReadToEnd();
            return (string.IsNullOrWhiteSpace(layout) ? null : layout, rest);
        }

        return (null, content);
    }

    private static string ReplaceTokens(string template, object? model)
    {
        return TokenRegex.Replace(template, match =>
        {
            if (match.Groups["raw"].Success)
            {
                return ResolvePathValue(model, match.Groups["raw"].Value.Trim()) ?? string.Empty;
            }

            var value = ResolvePathValue(model, match.Groups["enc"].Value.Trim()) ?? string.Empty;
            return WebUtility.HtmlEncode(value);
        });
    }

    private static string? ResolvePathValue(object? model, string path)
    {
        if (model is null || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.Equals("body", StringComparison.Ordinal))
        {
            return null;
        }

        object? current = model;
        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current is null)
            {
                return null;
            }

            current = GetMember(current, segment);
        }

        return current switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => current.ToString()
        };
    }

    private static object? GetMember(object target, string name)
    {
        if (target is System.Collections.IDictionary dict)
        {
            if (dict.Contains(name))
            {
                return dict[name];
            }

            foreach (var key in dict.Keys)
            {
                if (key is string s && s.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return dict[key];
                }
            }

            return null;
        }

        var type = target.GetType();
        var prop = type.GetProperty(name)
            ?? type.GetProperties().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (prop is not null)
        {
            return prop.GetValue(target);
        }

        var field = type.GetField(name)
            ?? type.GetFields().FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return field?.GetValue(target);
    }
}
