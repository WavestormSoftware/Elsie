using Fluid;
using Fluid.ViewEngine;
using Microsoft.Extensions.FileProviders;

namespace Elsie.Views;

/// <summary>
/// Fluid (Liquid) view engine: file loader under <see cref="ElsieViewOptions.RootPath"/>,
/// parsed-template cache (optional mtime reload), layouts + partials, HTML-encoding default.
/// </summary>
public sealed class FluidElsieViewEngine : IElsieViewEngine
{
    private readonly ElsieViewOptions _options;
    private readonly string _viewsRoot;
    private readonly object _gate = new();
    private FluidViewEngineOptions _fluidOptions;
    private FluidViewRenderer _renderer;
    private long _cacheStamp;

    public FluidElsieViewEngine(ElsieViewOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _viewsRoot = Path.GetFullPath(Path.Combine(_options.ContentRoot, _options.RootPath));
        if (!Directory.Exists(_viewsRoot))
        {
            Directory.CreateDirectory(_viewsRoot);
        }

        _fluidOptions = CreateFluidOptions();
        _renderer = new FluidViewRenderer(_fluidOptions);
        _cacheStamp = _options.ReloadOnChange ? ReadTreeStamp() : 0;
    }

    public async Task<string> RenderAsync(
        string viewName,
        object? model,
        ElsieViewAmbient? ambient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        cancellationToken.ThrowIfCancellationRequested();

        var relative = NormalizeViewName(viewName);
        EnsureInsideRoot(relative);

        if (_options.ReloadOnChange)
        {
            EnsureFreshCache();
        }

        var context = new TemplateContext(model ?? new object(), _fluidOptions.TemplateOptions);
        if (ambient is not null)
        {
            context.SetValue("Request", ambient);
        }

        await using var writer = new StringWriter();
        await _renderer.RenderViewAsync(writer, relative, context).ConfigureAwait(false);
        return writer.ToString();
    }

    private void EnsureFreshCache()
    {
        var stamp = ReadTreeStamp();
        if (stamp == _cacheStamp)
        {
            return;
        }

        lock (_gate)
        {
            stamp = ReadTreeStamp();
            if (stamp == _cacheStamp)
            {
                return;
            }

            // Drop Fluid's internal parse cache by rebuilding the renderer.
            _fluidOptions = CreateFluidOptions();
            _renderer = new FluidViewRenderer(_fluidOptions);
            _cacheStamp = stamp;
        }
    }

    private long ReadTreeStamp()
    {
        if (!Directory.Exists(_viewsRoot))
        {
            return 0;
        }

        long stamp = 0;
        foreach (var file in Directory.EnumerateFiles(_viewsRoot, "*", SearchOption.AllDirectories))
        {
            var ticks = File.GetLastWriteTimeUtc(file).Ticks;
            if (ticks > stamp)
            {
                stamp = ticks;
            }
        }

        return stamp;
    }

    private FluidViewEngineOptions CreateFluidOptions()
    {
        var provider = new PhysicalFileProvider(_viewsRoot);
        var fluidOptions = new FluidViewEngineOptions
        {
            ViewsFileProvider = provider,
            PartialsFileProvider = provider,
            // We invalidate via tree mtime + new renderer; disable Fluid file watchers.
            TrackFileChanges = false
        };

        // "{0}" keeps caller-relative paths (home.liquid / Shared/x.liquid) working.
        fluidOptions.ViewsLocationFormats.Add("{0}");
        fluidOptions.PartialsLocationFormats.Add("{0}");
        fluidOptions.PartialsLocationFormats.Add("{0}" + NormalizeExtension(_options.Extension));
        fluidOptions.LayoutsLocationFormats.Add("{0}");
        fluidOptions.LayoutsLocationFormats.Add("{0}" + NormalizeExtension(_options.Extension));

        // Allow anonymous / POCOs without [Fluid] attributes.
        fluidOptions.TemplateOptions.MemberAccessStrategy = UnsafeMemberAccessStrategy.Instance;
        return fluidOptions;
    }

    private string NormalizeViewName(string viewName)
    {
        var name = viewName.Trim().Replace('\\', '/').TrimStart('/');
        if (name.Contains("..", StringComparison.Ordinal) ||
            name.Contains(':', StringComparison.Ordinal) ||
            Path.IsPathRooted(viewName))
        {
            throw new InvalidOperationException($"View name '{viewName}' is invalid.");
        }

        var ext = NormalizeExtension(_options.Extension);
        if (!Path.HasExtension(name))
        {
            name += ext;
        }

        return name;
    }

    private void EnsureInsideRoot(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_viewsRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = _viewsRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, _viewsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("View name escapes the views root.");
        }

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"View '{relative}' was not found at '{full}'.", full);
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".liquid";
        }

        return extension.StartsWith('.') ? extension : "." + extension;
    }
}
