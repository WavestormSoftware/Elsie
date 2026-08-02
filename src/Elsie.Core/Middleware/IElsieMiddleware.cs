namespace Elsie.Middleware;

/// <summary>
/// A request middleware component. Code before <c>await next(context)</c> runs on the way in
/// (FIFO in registration order); code after it runs on the way back out (LIFO).
/// Short-circuit by setting <see cref="ElsieContext.Result"/> and returning without calling
/// <c>next</c>.
/// </summary>
public interface IElsieMiddleware
{
    /// <summary>Invoke the middleware with the pipeline's next step.</summary>
    Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next);
}
