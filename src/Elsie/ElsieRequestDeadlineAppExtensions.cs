using Elsie.Middleware;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Web;

/// <summary>App-level registration for the per-request deadline middleware.</summary>
public static class ElsieRequestDeadlineAppExtensions
{
    /// <summary>
    /// Enable a per-request deadline: a handler that exceeds the given span is aborted with
    /// <c>408 Request Timeout</c> (when the response has not been started). WebSocket upgrades
    /// and streaming (<c>text/event-stream</c> SSE, large BodyWriter) responses are exempt.
    /// </summary>
    public static ElsieApp UseRequestDeadline(
        this ElsieApp app,
        TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseRequestDeadline(o => o.Deadline = deadline);
    }

    /// <summary>
    /// Enable a per-request deadline with the given options (default <see cref="ElsieRequestDeadlineOptions.Deadline"/>
    /// is 30s).
    /// </summary>
    public static ElsieApp UseRequestDeadline(
        this ElsieApp app,
        Action<ElsieRequestDeadlineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Services(s => s.AddRequestDeadline(configure));
    }
}
