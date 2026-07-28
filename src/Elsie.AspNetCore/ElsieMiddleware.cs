using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elsie.AspNetCore;

public sealed class ElsieMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ElsieDispatcher _dispatcher;
    private readonly IElsieResultExecutor _executor;
    private readonly ILogger<ElsieMiddleware> _logger;
    private readonly bool _terminal;

    public ElsieMiddleware(
        RequestDelegate next,
        ElsieDispatcher dispatcher,
        IServiceProvider services,
        ILogger<ElsieMiddleware> logger,
        bool terminal = false)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _executor = services.GetService<IElsieResultExecutor>() ?? new ElsieResultExecutor();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _terminal = terminal;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = Stopwatch.GetTimestamp();
        var request = HttpContextElsieRequestFactory.Create(context);
        var outcome = await _dispatcher.DispatchAsync(request, context.RequestAborted).ConfigureAwait(false);

        switch (outcome.Status)
        {
            case ElsieDispatchStatus.NotFound:
                if (_terminal)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    Log(context, StatusCodes.Status404NotFound, start);
                    return;
                }

                await _next(context).ConfigureAwait(false);
                return;

            case ElsieDispatchStatus.MethodNotAllowed:
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = string.Join(", ", outcome.AllowedMethods);
                Log(context, StatusCodes.Status405MethodNotAllowed, start);
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
                Log(context, outcome.Result!.StatusCode, start);
                return;

            default:
                throw new InvalidOperationException($"Unknown dispatch status '{outcome.Status}'.");
        }
    }

    private void Log(HttpContext context, int statusCode, long startTimestamp)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        _logger.LogInformation(
            "{Method} {Path} → {StatusCode} {ElapsedMs:0}ms",
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            statusCode,
            elapsedMs);
    }
}
