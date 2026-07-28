using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.AspNetCore;

public sealed class ElsieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ElsieDispatcher _dispatcher;
    private readonly IElsieResultExecutor _executor;
    private readonly bool _terminal;

    public ElsieMiddleware(
        RequestDelegate next,
        ElsieDispatcher dispatcher,
        IServiceProvider services,
        bool terminal = false)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _executor = services.GetService<IElsieResultExecutor>() ?? new ElsieResultExecutor();
        _terminal = terminal;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var request = HttpContextElsieRequestFactory.Create(context);
        var outcome = await _dispatcher.DispatchAsync(request, context.RequestAborted).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case ElsieDispatchStatus.NotFound:
                if (_terminal)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await _next(context).ConfigureAwait(false);
                return;

            case ElsieDispatchStatus.MethodNotAllowed:
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = string.Join(", ", outcome.AllowedMethods);
                return;

            case ElsieDispatchStatus.Handled:
                if (outcome.Response is not null)
                {
                    foreach (var header in outcome.Response.Headers)
                    {
                        context.Response.Headers[header.Key] = header.Value;
                    }
                }

                await _executor.ExecuteAsync(context, outcome.Result!, context.RequestAborted).ConfigureAwait(false);
                return;

            default:
                throw new InvalidOperationException($"Unknown dispatch status '{outcome.Status}'.");
        }
    }
}
