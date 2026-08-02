namespace Elsie.Middleware;

/// <summary>
/// Terminal exception-handler middleware: catches exceptions thrown by downstream middleware and
/// maps them through <see cref="ElsieOptions.TryMapExceptionAsync"/> then
/// <see cref="ElsieOptions.ExceptionHandler"/>, rethrowing when neither applies.
/// Register it **first** (outermost) in the app pipeline so it wraps everything downstream.
/// </summary>
public sealed class ElsieExceptionHandlerMiddleware : IElsieMiddleware
{
    private readonly ElsieOptions _options;

    /// <summary>Create the middleware over the app's <see cref="ElsieOptions"/>.</summary>
    public ElsieExceptionHandlerMiddleware(ElsieOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task InvokeAsync(ElsieContext context, ElsieMiddlewareDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var mapped = await _options.TryMapExceptionAsync(context, ex, context.RequestAborted);
            if (mapped is not null)
            {
                context.Result = mapped;
                return;
            }

            if (_options.ExceptionHandler is not null)
            {
                context.Result = await _options.ExceptionHandler(context, ex, context.RequestAborted);
                return;
            }

            throw;
        }
    }
}
