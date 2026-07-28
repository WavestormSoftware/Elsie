namespace Elsie.Pipelines;

/// <summary>
/// Runs before a route handler. Return a result to short-circuit; return null to continue.
/// </summary>
public delegate Task<ElsieResult?> ElsieBeforeDelegate(ElsieContext context, CancellationToken cancellationToken);

/// <summary>
/// Runs after a route handler has produced a result (including short-circuit befores).
/// </summary>
public delegate Task ElsieAfterDelegate(ElsieContext context, ElsieResult result, CancellationToken cancellationToken);
