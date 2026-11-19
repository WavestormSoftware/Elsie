# Realtime: WebSockets and SSE

## WebSockets

```csharp
app.MapGet("/ws", () => ElsieResult.WebSocket(async (socket, ct) =>
{
    string? text;
    while ((text = await socket.ReceiveTextAsync(ct)) is not null)
    {
        await socket.SendTextAsync($"echo: {text}", ct);
    }

    Console.WriteLine($"{socket.Subprotocol} closed {socket.CloseStatus} {socket.CloseStatusDescription}");
}, "chat.v1"));
```

`ElsieWebSocket` implements RFC 6455 framing over any duplex stream (HTTP/1.1, HTTP/2
extended CONNECT, HTTP/3 and the in-memory test host):

- **Fragmentation**: continuation frames are assembled into one message (text or binary).
- **Control frames** (ping/pong/close) may interleave with fragments; pings are answered
  automatically (`ElsieWebSocketOptions.AutoPong`).
- **Masking**: unmasked client frames fail the connection with 1002
  (`RequireMaskedFrames`, on by default; disable only for non-conformant clients).
- **Validation**: RSV bits without a negotiated extension → 1002, oversized frames/messages →
  1009, invalid UTF-8 text → 1007, fragmented/payload-too-large control frames → 1002.
- **Close handshake**: `CloseAsync(status, reason)` (idempotent), peer close frames are echoed and
  exposed through `CloseStatus` / `CloseStatusDescription`.
- **Concurrent sends**: writes are serialized per connection, so a hub can broadcast from many
  tasks. `BytesSent` / `BytesReceived` expose payload counters.
- **Subprotocols**: `ElsieResult.WebSocket(handler, "chat.v1", "chat.v2")` negotiates the first
  client-offered match, echoes `Sec-WebSocket-Protocol`, and exposes it as `socket.Subprotocol`.
- Helpers: `SendTextAsync`, `SendBinaryAsync`, `PingAsync`, `ReceiveAsync`,
  `ReceiveTextAsync`, `ReceiveBinaryAsync`. Protocol violations throw `ElsieWebSocketException`
  with the status code used to fail the connection.

Limits live on `ElsieWebSocketOptions` (`MaxMessageBytes`, `MaxFrameBytes`, defaults 16 MiB).

## Rooms and broadcast

```csharp
services.AddSingleton<ElsieWebSocketHub>();

app.MapGet("/chat/{room}", (string room, ElsieWebSocketHub hub) => ElsieResult.WebSocket(async (socket, ct) =>
{
    using var membership = hub.Join(room, socket);
    string? text;
    while ((text = await socket.ReceiveTextAsync(ct)) is not null)
    {
        await hub.BroadcastAsync(room, text, ct);
    }
}));
```

`ElsieWebSocketHub` keeps named `ElsieWebSocketGroup`s. Group broadcasts deliver in parallel,
drop closed sockets, and ignore per-connection write failures; `Join` returns a lease you dispose
to leave the room. `BroadcastAllAsync` fans out across every room.

## Server-Sent Events

```csharp
app.MapGet("/events", (ElsieRequest request) => ElsieResult.ServerSentEvents(async (sse, ct) =>
{
    var lastId = ElsieSse.GetLastEventId(request);
    await sse.WriteRetryAsync(3000, ct);
    await sse.WriteEventAsync(new ElsieSseEvent("payload", EventName: "update", Id: "8"), ct);
    await sse.WriteHeartbeatAsync(ct); // ": heartbeat"
}));
```

- `WriteEventAsync(data, eventName, id, retryMs)` writes spec-ordered `id:` / `event:` / `retry:` /
  `data:` lines; multi-line data is split into one `data:` line per line.
- `ElsieSseEvent` record overload for passing events around.
- `ElsieSse.GetLastEventId(request)` reads the `Last-Event-ID` header (or `lastEventId` query) for
  resume support, and `AcceptsEventStream(request)` checks `Accept`.
- `WriteHeartbeatAsync` emits a comment for keep-alive.

SSE responses are never compressed (per-event flush) and work through the in-memory test host.
