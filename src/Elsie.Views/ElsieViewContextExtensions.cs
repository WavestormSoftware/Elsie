using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Views;

public static class ElsieViewContextExtensions
{
    /// <summary>Render a Liquid view (and optional layout) to an HTML <see cref="ElsieResult"/>.</summary>
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
        var ambient = new ElsieViewAmbient
        {
            Path = ctx.Request.Path,
            QueryString = ctx.Request.QueryString,
            Method = ctx.Request.Method,
            Scheme = ctx.Request.Scheme,
            Host = ctx.Request.Host
        };
        var html = await engine.RenderAsync(viewName, model, ambient, cancellationToken).ConfigureAwait(false);
        return ElsieResult.Html(html, statusCode);
    }
}
