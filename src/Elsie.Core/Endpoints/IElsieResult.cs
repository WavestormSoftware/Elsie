namespace Elsie;

/// <summary>
/// A lazily executed result that can build its <see cref="ElsieResult"/> from the current
/// request (app JSON options, request path, trace id). Return one from a route handler, or
/// produce one with <see cref="ElsieResults"/>.
/// </summary>
public interface IElsieResult
{
    /// <summary>Materialize the response for <paramref name="context"/>.</summary>
    Task<ElsieResult> ExecuteAsync(ElsieContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory helpers for <see cref="IElsieResult"/>. Unlike <see cref="ElsieResult"/> statics,
/// these serialize with the request's app JSON options (<see cref="ElsieContext.JsonSerializerOptions"/>).
/// </summary>
public static class ElsieResults
{
    /// <summary>200 with no body.</summary>
    public static IElsieResult Ok() => Status(200);

    /// <summary>200 with a JSON body.</summary>
    public static IElsieResult Ok<T>(T value) => Json(value, 200);

    /// <summary>201 with an optional JSON body and optional <c>Location</c> header.</summary>
    public static IElsieResult Created<T>(T value, string? location = null) =>
        new LambdaResult((ctx, _) =>
        {
            var result = ctx.Json(value, 201);
            return Task.FromResult(location is null ? result : result.WithHeader("Location", location));
        });

    /// <summary>201 with an optional <c>Location</c> header and no body.</summary>
    public static IElsieResult Created(string? location = null)
    {
        var result = ElsieResult.Status(201);
        return new LambdaResult((_, _) => Task.FromResult(
            location is null ? result : result.WithHeader("Location", location)));
    }

    /// <summary>202 with an optional JSON body and optional <c>Location</c> header.</summary>
    public static IElsieResult Accepted<T>(T value, string? location = null) =>
        new LambdaResult((ctx, _) =>
        {
            var result = ctx.Json(value, 202);
            return Task.FromResult(location is null ? result : result.WithHeader("Location", location));
        });

    /// <summary>204 No Content.</summary>
    public static IElsieResult NoContent() => Status(204);

    /// <summary>304 Not Modified.</summary>
    public static IElsieResult NotModified() => Status(304);

    /// <summary>Any status code with no body.</summary>
    public static IElsieResult Status(int statusCode) =>
        new LambdaResult((_, _) => Task.FromResult(ElsieResult.Status(statusCode)));

    /// <summary>404 with no body.</summary>
    public static IElsieResult NotFound() => Status(404);

    /// <summary>404 with a JSON body.</summary>
    public static IElsieResult NotFound<T>(T value) => Json(value, 404);

    /// <summary>400 with no body.</summary>
    public static IElsieResult BadRequest() => Status(400);

    /// <summary>400 with a JSON body.</summary>
    public static IElsieResult BadRequest<T>(T value) => Json(value, 400);

    /// <summary>401 with no body.</summary>
    public static IElsieResult Unauthorized() => Status(401);

    /// <summary>403 with no body.</summary>
    public static IElsieResult Forbid() => Status(403);

    /// <summary>409 with no body.</summary>
    public static IElsieResult Conflict() => Status(409);

    /// <summary>409 with a JSON body.</summary>
    public static IElsieResult Conflict<T>(T value) => Json(value, 409);

    /// <summary>422 with no body.</summary>
    public static IElsieResult UnprocessableEntity() => Status(422);

    /// <summary>422 with a JSON body.</summary>
    public static IElsieResult UnprocessableEntity<T>(T value) => Json(value, 422);

    /// <summary>RFC 7807-style problem response (uses request JSON options + instance/traceId).</summary>
    public static IElsieResult Problem(int statusCode, string title, string? detail = null) =>
        new LambdaResult((ctx, _) => Task.FromResult(ctx.Problem(statusCode, title, detail)));

    /// <summary>400 problem+json with an <c>errors</c> object.</summary>
    public static IElsieResult ValidationProblem(IReadOnlyDictionary<string, string[]> errors, string? detail = null) =>
        new LambdaResult((ctx, _) => Task.FromResult(ElsieResult.ValidationProblem(
            errors,
            detail,
            ctx.JsonSerializerOptions)));

    /// <summary><c>text/plain</c> body.</summary>
    public static IElsieResult Text(string text, int statusCode = 200) =>
        new LambdaResult((_, _) => Task.FromResult(ElsieResult.Text(text, statusCode)));

    /// <summary>JSON body serialized with the request's app JSON options.</summary>
    public static IElsieResult Json<T>(T value, int statusCode = 200) =>
        new LambdaResult((ctx, _) => Task.FromResult(ctx.Json(value, statusCode)));

    /// <summary>Buffered payload with an explicit content type.</summary>
    public static IElsieResult Bytes(ReadOnlyMemory<byte> bytes, string contentType, int statusCode = 200) =>
        new LambdaResult((_, _) => Task.FromResult(ElsieResult.Bytes(bytes, contentType, statusCode)));

    /// <summary>Buffered file payload with optional download name.</summary>
    public static IElsieResult File(
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string? downloadName = null,
        int statusCode = 200) =>
        new LambdaResult((_, _) => Task.FromResult(ElsieResult.File(bytes, contentType, downloadName, statusCode)));

    /// <summary>Streamed payload.</summary>
    public static IElsieResult Stream(Func<Stream, CancellationToken, Task> writer, string contentType, int statusCode = 200) =>
        new LambdaResult((_, _) => Task.FromResult(ElsieResult.Stream(writer, contentType, statusCode)));

    /// <summary>Redirect (302 by default, 301 when permanent).</summary>
    public static IElsieResult Redirect(string location, bool permanent = false) =>
        new LambdaResult((_, _) => Task.FromResult(ElsieResult.Redirect(location, permanent)));

    private sealed class LambdaResult(Func<ElsieContext, CancellationToken, Task<ElsieResult>> execute) : IElsieResult
    {
        public Task<ElsieResult> ExecuteAsync(ElsieContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            return execute(context, cancellationToken);
        }
    }
}

/// <summary>Maps handler return values onto <see cref="ElsieResult"/>.</summary>
internal static class ElsieEndpointResultMapper
{
    public static async Task<ElsieResult> ToResultAsync(object? value, ElsieContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        switch (value)
        {
            case null:
                return context.Result ?? ElsieResult.NoContent();
            case ElsieResult result:
                return result;
            case IElsieResult lazy:
                return await lazy.ExecuteAsync(context, context.DispatchCancellationToken).ConfigureAwait(false);
            case string text:
                return ElsieResult.Text(text);
            default:
                return context.Json(value);
        }
    }
}
