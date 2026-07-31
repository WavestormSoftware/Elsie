# Testing

Package **`Elsie.Testing`**.

## In-memory host (dispatcher only)

Fast unit tests — no sockets:

```csharp
await using var host = ElsieInMemoryHost.Create(s =>
{
    s.AddElsieModule<TodosModule>();
    s.AddSingleton<ITodoStore, FakeStore>();
});

var res = await host.GetAsync("/api/todos");
Assert.Equal(200, res.StatusCode);
```

Creates a DI scope per request (`ValidateScopes = true`). Entry-assembly scan is off by default.

## Loopback host (real HTTP/1.1)

Exercises the custom server over TCP:

```csharp
await using var host = ElsieTestHost.Create(s =>
{
    s.AddElsieAuth(o =>
    {
        o.Cookie = new ElsieCookieAuthOptions { CookieName = "t" };
        o.Cookie.TicketKeyFromString("test-key");
    });
    s.AddElsieModule<SecureModule>();
});

var login = await host.PostJsonAsync("/login", new { user = "ada", password = "pass" });
login.EnsureSuccessStatusCode();
var me = await host.GetAsync("/me");
```

`HttpClient` has cookies enabled for session tests.

## Fluent `ElsieApp` in tests

```csharp
await using var server = await ElsieApp.Create()
    .QuietConsole(false)
    .Listen(IPAddress.Loopback, 0)
    .Configure(o => o.ScanEntryAssembly = false)
    .Module<PingModule>()
    .StartAsync();

using var client = server.CreateClient();
Assert.Equal("pong", await client.GetStringAsync("/ping"));
```

## Assert helpers

```csharp
response.AssertStatus(200);
await response.AssertTextAsync("ok");
await response.AssertJsonAsync<Todo>();
response.AssertHeader("X-Test", "yes");
```

## Multipart

```csharp
var form = new MultipartFormBuilder()
    .AddField("title", "hi")
    .AddFile("file", "a.txt", Encoding.UTF8.GetBytes("data"))
    .Build();
var res = await host.Client.PostAsync("/upload", form);
```

## See also

- [getting-started.md](getting-started.md)
- [auth.md](auth.md)
