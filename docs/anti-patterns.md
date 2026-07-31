# Anti-patterns

## Captive dependencies in modules

Modules are **singletons**. Do **not** ctor-inject scoped services (DbContext, etc.).

```csharp
// bad
public sealed class Todos : ElsieModule {
  public Todos(AppDb db) { ... }
}

// good
Get("/todos", ctx => {
  var db = ctx.GetRequiredService<AppDb>();
  ...
});
```

## Static `ElsieResult.Json` vs app JSON options

`ElsieResult.Json` uses framework defaults. App `JsonSerializerOptions` (source-gen, naming) apply via `ctx.Json(...)`.

## Trusting `X-Forwarded-For` on the public internet

Enabling `UseForwardedHeaders` or `ForwardedPartitionKey` without a stripping proxy lets clients spoof IP / scheme / host.

## Rate limit + XFF

`DefaultPartitionKey` uses **RemoteIp only**. Do not reintroduce XFF into the default.

## Cookie auth without CSRF

Browser cookie sessions need antiforgery on POST/PUT/PATCH/DELETE.

## Assembly scan in trimmed/AOT apps

Prefer explicit `AddElsieModule<T>()` / `.Module<T>()`.

## Buffering huge static files

Host streams static files; avoid re-implementing with `File.ReadAllBytes` in app code.
