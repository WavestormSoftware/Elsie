# Results

Handlers return **`ElsieResult`**. The host materializes via `ElsieHttpResponse.FromDispatch`.

## Factories

```csharp
ElsieResult.Text("ok")
ElsieResult.Html("<p>hi</p>")
ElsieResult.Json(payload, statusCode: 201)   // framework JSON defaults
ctx.Json(payload)                            // app JsonSerializerOptions
ElsieResult.Created("/items/1", body)
ElsieResult.Accepted()
ElsieResult.File(bytes, "application/pdf", downloadName: "a.pdf")
ElsieResult.Redirect("/elsewhere")           // 302; 307/308 helpers also
ElsieResult.NoContent()
ElsieResult.NotModified()
ElsieResult.Problem(400, "Bad Request", detail)
ElsieResult.ValidationProblem(errors)
ElsieResult.ServerSentEvents(async (w, ct) => await w.WriteEventAsync("tick", "1", ct))
ElsieResult.WebSocket(async (ws, ct) => { /* … */ })
```

| API | JSON options |
|-----|----------------|
| `ElsieResult.Json(...)` | `ElsieJson.DefaultOptions` unless `options:` passed |
| `ctx.Json(...)` | App options from `.Configure(o => o.JsonSerializerOptions = …)` |

## Headers / cookies

```csharp
return result.WithHeader("X-App", "1");
return result.WithCookie("sid", value, new ElsieCookieOptions { HttpOnly = true });
ctx.Response.Headers["X-App"] = "1";
ctx.Response.SetCookie("sid", value, options);
```

## See also

- [binding.md](binding.md)
- [hosting-and-aot.md](hosting-and-aot.md) — WebSockets / SSE notes
