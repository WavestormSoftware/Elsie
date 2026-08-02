namespace Elsie.Pipelines;

/// <summary>
/// Before-hook style gate adapted into the middleware pipeline via
/// <see cref="Middleware.ElsieMiddlewarePipeline.Use(ElsieBeforeDelegate)"/>.
/// Return a result to short-circuit; return null to continue.
/// </summary>
public delegate Task<ElsieResult?> ElsieBeforeDelegate(ElsieContext context, CancellationToken cancellationToken);
