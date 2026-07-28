using System.Text;
using System.Text.Json;
using Elsie;
using Microsoft.Extensions.DependencyInjection;

// -----------------------------------------------------------------------------
// Core sample — no ASP.NET Core. Drive Elsie via ElsieDispatcher + ElsieRequest.
//
//   dotnet run --project samples/Elsie.Sample.Core
// -----------------------------------------------------------------------------

var services = new ServiceCollection();
services.AddSingleton<ICounter, Counter>();
services.AddElsie(o =>
{
    o.ScanEntryAssembly = false;
    o.ExceptionHandler = (_, ex, _) =>
        Task.FromResult(ElsieResult.Problem(500, "Server Error", ex.Message));
});
services.AddElsieModule<DemoModule>();
services.ConfigureElsiePipelines(p =>
{
    p.AddAfter((ctx, _) => ctx.Response.Headers["X-Elsie-Sample"] = "core");
});

await using var sp = services.BuildServiceProvider();
var dispatcher = sp.GetRequiredService<ElsieDispatcher>();

Console.WriteLine("Elsie core dispatcher sample (no ASP.NET)\n");

await PrintAsync(dispatcher, Req(sp, "GET", "/"));
await PrintAsync(dispatcher, Req(sp, "GET", "/hello/Ada"));
await PrintAsync(dispatcher, Req(
    sp,
    method: "GET",
    path: "/hello/Ada",
    query: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["shout"] = "true" }));
await PrintAsync(dispatcher, Req(sp, "GET", "/count"));
await PrintAsync(dispatcher, Req(sp, "GET", "/count"));
await PrintAsync(dispatcher, JsonRequest(sp, "POST", "/echo", new { message = "from core host" }));
await PrintAsync(dispatcher, JsonRequest(sp, "POST", "/echo", new { message = "" }));
await PrintAsync(dispatcher, Req(sp, "PUT", "/echo")); // 405
await PrintAsync(dispatcher, Req(sp, "GET", "/missing")); // 404

static ElsieRequest Req(
    IServiceProvider services,
    string method,
    string path,
    IReadOnlyDictionary<string, string>? query = null) =>
    new(
        method: method,
        path: path,
        query: query,
        requestServices: services);

static ElsieRequest JsonRequest(IServiceProvider services, string method, string path, object body)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(body, ElsieJson.DefaultOptions);
    var stream = new MemoryStream(bytes);
    return new ElsieRequest(
        method: method,
        path: path,
        body: stream,
        contentLength: bytes.Length,
        contentType: "application/json",
        requestServices: services);
}

static async Task PrintAsync(ElsieDispatcher dispatcher, ElsieRequest request)
{
    var query = request.Query.Count == 0
        ? string.Empty
        : "?" + string.Join('&', request.Query.Select(kv => $"{kv.Key}={kv.Value}"));

    Console.WriteLine($"→ {request.Method} {request.Path}{query}");
    var outcome = await dispatcher.DispatchAsync(request);

    switch (outcome.Status)
    {
        case ElsieDispatchStatus.NotFound:
            Console.WriteLine("  ← 404 Not Found (dispatch)");
            break;
        case ElsieDispatchStatus.MethodNotAllowed:
            Console.WriteLine($"  ← 405 Method Not Allowed; Allow: {string.Join(", ", outcome.AllowedMethods)}");
            break;
        case ElsieDispatchStatus.Handled:
            {
                var result = outcome.Result!;
                var body = await ReadBodyAsync(result);
                Console.WriteLine($"  ← {result.StatusCode} {result.ContentType}");
                if (outcome.Response!.Headers.Count > 0)
                {
                    Console.WriteLine($"     headers: {string.Join(", ", outcome.Response.Headers.Select(h => $"{h.Key}={h.Value}"))}");
                }

                if (result.Headers.Count > 0)
                {
                    Console.WriteLine($"     result:  {string.Join(", ", result.Headers.Select(h => $"{h.Key}={h.Value}"))}");
                }

                if (!string.IsNullOrEmpty(body))
                {
                    Console.WriteLine($"     body:    {body}");
                }

                break;
            }
    }

    Console.WriteLine();
}

static async Task<string> ReadBodyAsync(ElsieResult result)
{
    if (result.BodyWriter is not null)
    {
        await using var ms = new MemoryStream();
        await result.BodyWriter(ms, CancellationToken.None);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    return result.Body is { } mem && mem.Length > 0
        ? Encoding.UTF8.GetString(mem.Span)
        : string.Empty;
}

public interface ICounter
{
    int Next();
}

public sealed class Counter : ICounter
{
    private int _value;
    public int Next() => Interlocked.Increment(ref _value);
}

public sealed class DemoModule : ElsieModule
{
    public DemoModule(ICounter counter)
    {
        Get("/", () => ElsieResult.Text("Elsie core sample. Routes: /hello/{name}, /count, POST /echo"));

        Get("/hello/{name}", ctx =>
        {
            var name = ctx.RouteOrDefault("name") ?? "world";
            var shout = ctx.TryGetQueryBool("shout", out var s) && s;
            var text = $"Hello {name}!";
            return ElsieResult.Text(shout ? text.ToUpperInvariant() : text);
        });

        Get("/count", ctx =>
        {
            // Request-scoped resolve works the same without ASP.NET when services are on ElsieRequest.
            var c = ctx.GetRequiredService<ICounter>();
            return ctx.Json(new { n = c.Next(), path = ctx.Request.Path });
        });

        Post("/echo", async (ctx, ct) =>
        {
            var bind = await ctx.BindJsonAsync<EchoDto>(ct);
            if (!bind.IsSuccess)
            {
                return bind.Error!;
            }

            if (string.IsNullOrWhiteSpace(bind.Value!.Message))
            {
                return ElsieResult.BadRequest("message is required");
            }

            return ctx.Json(bind.Value);
        });
    }
}

public sealed record EchoDto(string Message);
