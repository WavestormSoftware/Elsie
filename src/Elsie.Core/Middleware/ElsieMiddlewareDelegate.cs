namespace Elsie.Middleware;

/// <summary>
/// Invokes the next middleware (or the terminal handler step) in the pipeline.
/// Middleware short-circuits by setting <see cref="ElsieContext.Result"/> and returning
/// without calling <c>next</c>.
/// </summary>
public delegate Task ElsieMiddlewareDelegate(ElsieContext context);
