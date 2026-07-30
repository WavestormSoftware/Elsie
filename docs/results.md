# Results

Handlers return **`ElsieResult`**. The host materializes it once via **`ElsieHttpResponse.FromDispatch`**.

## Factories

| Factory | Notes |
|---------|--------|
| `Text(string)` | `text/plain` |
| `Html(string)` | `text/html` |
| `Json<T>(value, statusCode?, options?)` | Uses **`ElsieJson.DefaultOptions`** when `options` omitted |
| `ctx.Json(value, …)` | Uses app **`ElsieOptions.JsonSerializerOptions`** |
| `Bytes` / `File` / `Stream` | Binary / download / streaming body |
| `Created(location?, body?)` | 201 + optional `Location` |
| `Accepted(location?, body?)` | 202 |
| `Redirect` / `RedirectTemporary` / `RedirectPermanent` | 302 / 307 / 308 |
| `NoContent` | 204 |
| `NotModified` | 304 |
| `Status(code)` | Empty body |
| `Problem` / `ValidationProblem` | `application/problem+json` |
| `BadRequest` / `Unauthorized` / `Forbidden` / `NotFound` / `Conflict` / `NotAcceptable` | Problem helpers |
| `ServerSentEvents(writer)` | `text/event-stream` |
| `IfNoneMatch(header, etag, whenModified)` | Conditional helper |

## Headers & cookies

```csharp
return ElsieResult.Json(item)
    .WithHeader("X-Trace", "1")
    .WithHeaders(new Dictionary<string, string?> { ["X-A"] = "1" })
    .WithCookie("sid", "abc", new ElsieCookieOptions { HttpOnly = true, Secure = true });
```

Multi-value headers are first-class (`ElsieHeaders`). Cookies become `Set-Cookie` at bake time via `ElsieResponse` hooks as well:

```csharp
ctx.Response.SetCookie("sid", value, new ElsieCookieOptions { HttpOnly = true });
ctx.Response.DeleteCookie("sid");
```

## JSON options rule

| Call site | Options |
|-----------|---------|
| `ElsieResult.Json(...)` | Framework defaults (`ElsieJson.DefaultOptions`) unless you pass `options` |
| `ctx.Json(...)` | App options from `AddElsie(o => o.JsonSerializerOptions = …)` |

There is no process-wide mutable `ElsieJson.Configure`.

## Negotiation

```csharp
return ctx.Json(model);
// ctx.Problem(status, title, detail?) adds instance + optional traceId
```

## See also

- [binding.md](binding.md)
- [pipelines-and-errors.md](pipelines-and-errors.md)
- [views.md](views.md)
