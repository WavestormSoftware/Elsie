# HTTP/3

Elsie speaks HTTP/3 over QUIC (`System.Net.Quic`) with a clean-room **full QPACK** codec
(RFC 9204, static + dynamic tables) and HTTP/3 framing (RFC 9114). No third-party networking
libraries.

## Enable

HTTP/3 requires TLS (certificate) and the `h3` ALPN:

```csharp
ElsieApp.Create(args)
    .Listen(IPAddress.Any, 443, o =>
    {
        o.UseHttps = true;
        o.CertificateFromPfx("/etc/elsie/cert.pfx");
        o.Protocols = ElsieHttpProtocols.Http1AndHttp2; // TCP side
        o.EnableHttp3 = true;                            // UDP side on the same port
    })
    .Module<App>()
    .Run();
```

`EnableHttp3` opens a UDP `QuicListener` on the same address/port as the TCP listener.
When `QuicListener.IsSupported` is false (no `libmsquic` on the machine — e.g. local dev
without the runtime), the UDP listener is **silently skipped** and HTTP/1.1/2 keep working.

> Note: with `Port = 0` (ephemeral) the TCP and UDP ports may differ. Use a fixed port
> for production h3 deployments.

## QPACK configuration

The server advertises a QPACK dynamic-table capacity (the limit for *clients'* encoders)
and a blocked-stream allowance via `ElsieServerOptions`:

```csharp
o.Server(s => s.QpackMaxTableCapacity = 4096); // SETTINGS_QPACK_MAX_TABLE_CAPACITY (default 4096)
o.Server(s => s.QpackBlockedStreams = 100);    // SETTINGS_QPACK_BLOCKED_STREAMS (default 100)
```

Set `QpackMaxTableCapacity = 0` to keep the minimal capacity-0 interop path (clients must
not insert into a dynamic table). The server's *own* encoder only starts inserting after the
client advertises a nonzero capacity in its SETTINGS — fully static/literal encoding (and
no encoder stream) is used otherwise.

## What is implemented

- **QUIC listener + accept loop** in `ElsieServer` (guarded by `QuicListener.IsSupported`).
- **Server control stream**: SETTINGS (QPACK max table capacity, blocked streams, max field
  section size, `SETTINGS_ENABLE_CONNECT_PROTOCOL=1`).
- **Client unidirectional streams**: control (SETTINGS parsed — the client's QPACK capacity
  drives the server encoder), QPACK encoder stream (fed to the decoder), QPACK decoder stream
  (Section Acknowledgments / Insert Count Increment / Stream Cancellation drive the encoder's
  reference tracking), and unknown types drained per RFC 9114 §6.2.
- **Request streams**: HEADERS (QPACK-decoded) + DATA bodies, dispatched through the same
  `HostDispatch` pipeline as HTTP/1.1/2 — routes, middleware, auth, rate limiting, views,
  static files all work identically.
- **Full QPACK (RFC 9204)**:
  - Decoder: dynamic table + encoder-instruction parsing (Insert With/Without Name Reference,
    Duplicate, Set Dynamic Table Capacity), static + dynamic references including post-base
    indexing, Required Insert Count reconstruction, and **blocked-stream delivery** (request
    streams wait for the encoder stream to catch up before dispatch).
  - Encoder: inserts repeated response fields into the dynamic table up to the peer's
    advertised capacity, emits encoder-stream instructions, and emits dynamic references.
  - Required decoder instructions are emitted (Section Acknowledgment, Insert Count
    Increment, Stream Cancellation), so clients can safely evict acknowledged entries.
  - Verified against the RFC 9204 Appendix B test vectors and round-trip tests; CI exercises
    dynamic inserts with `curl --http3` and aioquic.
- **Streaming responses**: buffered bodies use Content-Length; `BodyWriter` responses (SSE,
  static files, chunked writers) stream as HTTP/3 DATA frames without being buffered.
- **Response trailers**: `ctx.Response.AddTrailer(...)` works on h3 (trailing HEADERS after
  the body) — gRPC over h3 uses this.
- **WebSocket over h3 (RFC 9220)**: extended CONNECT requests (`:method: CONNECT`,
  `:protocol: websocket`) upgrade the request stream to a WebSocket tunnel, reusing the
  same `ElsieWebSocket` framing as HTTP/1.1. Register the handler with
  `Map("CONNECT", "/ws", () => ElsieResult.WebSocket(handler))`. Unknown `:protocol` values
  get a 501. WebTransport is explicitly out of scope.

## Interop

CI runs `http3.yml` (ubuntu): installs `libmsquic`, then `curl --http3` and an aioquic
client against the TLS + h3 sample, asserting 200s and dynamic-table-exercising requests
(repeated header fields across multiple requests force real QPACK inserts on the client side).

## See also

- [hosting-and-aot.md](hosting-and-aot.md)
- [middleware.md](middleware.md)
- [grpc.md](grpc.md)
