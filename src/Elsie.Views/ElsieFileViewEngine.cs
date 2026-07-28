using System.Net;
using System.Text.RegularExpressions;

namespace Elsie.Views;

/// <summary>
/// Tiny file template engine: <c>{{Path}}</c>, <c>{{{raw}}}</c>, optional <c>@layout Name</c> + <c>{{body}}</c>.
/// </summary>
public sealed class ElsieFileViewEngine
{
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
        const string bodyMarker = "\uE000ELSIE_BODY\uE000";
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
        if (!relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            relative += ".html";
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
            return (string.IsNullOrWhiteSpace(layout) ? null : layout, reader.ReadToEnd());
        }

        return (null, content);
    }

    private static string ReplaceTokens(string template, object? model) =>
        Regex.Replace(
            template,
            @"\{\{\{(?<raw>[^}]+)\}\}\}|\{\{(?<enc>[^}]+)\}\}",
            match =>
            {
                if (match.Groups["raw"].Success)
                {
                    return ResolvePathValue(model, match.Groups["raw"].Value.Trim()) ?? string.Empty;
                }

                var value = ResolvePathValue(model, match.Groups["enc"].Value.Trim()) ?? string.Empty;
                return WebUtility.HtmlEncode(value);
            },
            RegexOptions.CultureInvariant);

    private static string? ResolvePathValue(object? model, string path)
    {
        if (model is null || string.IsNullOrWhiteSpace(path) || path == "body")
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

            var prop = current.GetType().GetProperty(segment);
            current = prop?.GetValue(current);
        }

        return current?.ToString();
    }
}
