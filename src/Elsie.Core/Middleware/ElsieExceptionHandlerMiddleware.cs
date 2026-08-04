namespace Elsie.Middleware;

/// <summary>
/// Terminal exception-handler middleware: catches exceptions thrown by downstream middleware and
/// route handlers and maps them. <see cref="ElsieRequestException"/> becomes a problem result;
/// other exceptions go through <see cref="ElsieOptions.ExceptionHandler"/> (default: safe 500),
/// rethrowing when the handler is null. Register it **first** (outermost) in the app pipeline so
/// it wraps everything downstream — <see cref="ElsieServiceCollectionExtensions.AddElsie(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{ElsieOptions}?)"/>
/// does this automatically.
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
            if (ex is ElsieRequestException protocol)
            {
                context.Result = ElsieResult.Problem(protocol.StatusCode, protocol.Title, protocol.Message);
                return;
            }

            // Response-header CR/LF injection attempts are protocol/client errors, not server
            // faults — reject with a 400 (the injection stays blocked by ElsieHeaders).
            if (ex is ElsieHeaderValidationException headerValidation)
            {
                context.Result = ElsieResult.Problem(400, "Bad Request", headerValidation.Message);
                return;
            }

            if (_options.ExceptionHandler is not null)
            {
                context.Result = await _options.ExceptionHandler(context, ex, context.DispatchCancellationToken);
                return;
            }

            throw;
        }
    }
}
