# Background jobs and localization

## Background jobs

```csharp
ElsieApp.Create(args)
    .BackgroundJobs(o => { o.Capacity = 2048; o.WorkerCount = 4; })
    .Services(s => s.AddScoped<IEmailSender, SmtpEmailSender>())
    .Use((app, _) =>
    {
        app.MapPost("/signup", async (ElsieContext ctx, IElsieBackgroundQueue jobs) =>
        {
            var queued = await jobs.EnqueueAsync(async (scoped, ct) =>
            {
                var mailer = scoped.GetRequiredService<IEmailSender>();
                await mailer.SendWelcomeAsync(ctx.Request.GetQuery("email")!, ct);
            }, ctx.RequestAborted);

            return queued ? ElsieResults.Accepted() : ElsieResults.Status(503);
        });
    });
```

- `ElsieBackgroundQueue` is a bounded `Channel`; `EnqueueAsync` returns `false` when the queue is
  full or shutting down, so handlers can shed load instead of blocking.
- Every work item runs in its own DI scope (`scoped` provider), so scoped services and
  `DbContext`-style lifetimes behave exactly as they do in a request.
- A failing job is counted (`Failed`) and never kills the worker loop; `Processed`, `Failed` and
  `Count` are the monitoring surface.
- `AddElsieBackgroundJobs(...)` registers the queue plus the hosted workers; `WorkerCount` controls
  concurrency and `StopAsync` drains the queue before the host exits.
- Register the queue in DI (`IElsieBackgroundQueue`) and enqueue from modules, endpoints or
  middleware. For scheduled work, enqueue from an `IHostedService` of your own.

## Localization

```csharp
services.AddElsieLocalization(o =>
{
    o.DefaultCulture = "en";
    o.SupportedCultures.AddRange(["en", "fr"]);
    o.Resources["fr"] = new Dictionary<string, string> { ["hello"] = "bonjour" };
});

app.MapGet("/hello", (ElsieContext ctx) => ctx.T("hello"));
```

Resolution order for the request culture:

1. `?culture=fr` (name configurable with `QueryParameterName`),
2. `Accept-Language` with q-values (`de;q=0.4, fr;q=0.9` → `fr`),
3. `DefaultCulture`.

Requested cultures are checked against `SupportedCultures` (neutral names match regional variants)
and unsupported ones fall back to the default. The middleware sets `CultureInfo.CurrentCulture` /
`CurrentUICulture`, adds `Content-Language` to the response and `Vary: Accept-Language` for correct
caching.

`ctx.T("key")` looks the key up for the request culture, then the parent culture, then
`DefaultCulture`, then returns the key itself — so missing translations degrade to readable text
instead of throwing. `ctx.SetCulture(...)` overrides the culture for the rest of the request
(useful after loading a user's stored preference).
