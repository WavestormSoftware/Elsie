# Testing

Package **`Elsie.Testing`**.

## In-memory (no web server)

```csharp
await using var mem = ElsieInMemoryHost.Create(s =>
{
    s.AddElsieModule<HelloModule>();
});

var r = await mem.GetAsync("/hello/Ada");
Assert.Equal(200, r.StatusCode);
Assert.Equal("Hello Ada!", r.ReadAsString());

var created = await mem.PostJsonAsync("/items", new { Title = "x" });
```

- Creates a **scope per request**, `ValidateScopes = true`
- Sets `ScanEntryAssembly = false` — register modules explicitly
- Returns **`ElsieInMemoryResponse`** (status, headers, body, dispatch status)

## ASP.NET TestServer

```csharp
await using var host = ElsieTestHost.Create(s =>
{
    s.AddElsieModule<HelloModule>();
});

var response = await host.GetAsync("/hello/Ada");
response.AssertStatus(200);
var text = await response.AssertTextAsync("Hello Ada!");
```

Optional host configuration:

```csharp
ElsieTestHost.Create(
    services => { /* DI */ },
    app => { app.UseElsieAuth(); app.MapElsie(); });
```

## Asserts

```csharp
response.AssertStatus(HttpStatusCode.Created);
response.AssertHeader("Location", "/items/1");
response.AssertHeaderContains("X-Trace", "abc");
var dto = await response.AssertJsonAsync<Todo>();
```

## Multipart

```csharp
using var content = new MultipartFormBuilder()
    .AddField("title", "hi")
    .AddFile("file", "a.txt", new byte[] { 1, 2, 3 }, "text/plain")
    .Build();

await host.Client.PostAsync("/upload", content);
```

## Tips

- Prefer **`AddElsieModule<T>()`** over assembly scan in tests
- Auth/CORS tests need the TestServer host + `UseElsieAuth` / `UseElsieCors`
- Rate-limit tests: inject a fake `TimeProvider` into the gate factory

## See also

- [modules.md](modules.md)
- [auth.md](auth.md)
