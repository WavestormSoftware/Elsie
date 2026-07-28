using Microsoft.AspNetCore.Http;

namespace Elsie.AspNetCore;

/// <summary>
/// ASP.NET Core escape hatch. Core stays free of <see cref="HttpContext"/>.
/// </summary>
public static class ElsieHttpContextExtensions
{
    private static readonly object HttpContextItemKey = new();

    /// <summary>
    /// Tries to read the ambient <see cref="HttpContext"/> attached by the Elsie ASP.NET adapter.
    /// Returns false for pure-core / in-memory hosts.
    /// </summary>
    public static bool TryGetHttpContext(this ElsieContext context, out HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.TryGetHttpContext(out httpContext);
    }

    /// <inheritdoc cref="TryGetHttpContext(ElsieContext, out HttpContext)"/>
    public static bool TryGetHttpContext(this ElsieRequest request, out HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Items.TryGetValue(HttpContextItemKey, out var value) && value is HttpContext ctx)
        {
            httpContext = ctx;
            return true;
        }

        httpContext = null!;
        return false;
    }

    /// <summary>
    /// Returns the ambient <see cref="HttpContext"/> or throws when not hosted on ASP.NET Core.
    /// </summary>
    public static HttpContext GetHttpContext(this ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.GetHttpContext();
    }

    /// <inheritdoc cref="GetHttpContext(ElsieContext)"/>
    public static HttpContext GetHttpContext(this ElsieRequest request)
    {
        if (request.TryGetHttpContext(out var httpContext))
        {
            return httpContext;
        }

        throw new InvalidOperationException(
            "No HttpContext is attached to this Elsie request. " +
            "Use MapElsie/UseElsie (Elsie.AspNetCore) or attach one via SetHttpContext.");
    }

    /// <summary>Stash <paramref name="httpContext"/> on the request bag (adapter / tests).</summary>
    public static void SetHttpContext(this ElsieRequest request, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(httpContext);
        request.Items[HttpContextItemKey] = httpContext;
    }
}
