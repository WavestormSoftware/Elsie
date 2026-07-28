using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Views;

public static class ElsieViewContextExtensions
{
    /// <summary>Render a view (and optional layout) to an HTML <see cref="ElsieResult"/>.</summary>
    public static async Task<ElsieResult> ViewAsync(
        this ElsieContext ctx,
        string viewName,
        object? model = null,
        int statusCode = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        var engine = ctx.GetRequiredService<IElsieViewEngine>();
        var html = await engine.RenderAsync(viewName, model, cancellationToken).ConfigureAwait(false);
        return ElsieResult.Text(html, statusCode, "text/html; charset=utf-8");
    }
}
