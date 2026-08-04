namespace Elsie.Middleware;

/// <summary>Options controlling the per-request deadline middleware.</summary>
public sealed class ElsieRequestDeadlineOptions
{
    /// <summary>
    /// The maximum time a handler may run before the request is aborted with <c>408 Request
    /// Timeout</c>. <see cref="TimeSpan.Zero"/> or negative disables the deadline (pass-through).
    /// </summary>
    public TimeSpan Deadline { get; set; } = TimeSpan.FromSeconds(30);
}
