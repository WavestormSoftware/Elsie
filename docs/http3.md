# HTTP/3

Elsie speaks HTTP/3 over QUIC (`System.Net.Quic`) with a clean-room **QPACK** codec
(RFC 9204) and HTTP/3 framing (RFC 9114). No third-party networking libraries.

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

## What is implemented

- **QUIC listener + accept loop** in `ElsieServer` (guarded by `QuicListener.IsSupported`).
- **Server control stream**: SETTINGS (QPACK max table capacity 0, blocked streams 0,
  max field section size 16 KiB), GOAWAY / MAX_PUSH_ID scaffolding.
- **Client unidirectional streams**: control (SETTINGS) + QPACK encoder/decoder streams are
  consumed per RFC 9114 §6.2.
- **Request streams**: HEADERS (QPACK-decoded) + DATA bodies, dispatched through the same
  `HostDispatch` pipeline as HTTP/1.1/2 — routes, middleware, auth, rate limiting, views,
  static files all work identically.
- **QPACK (RFC 9204)**: static table + literal field lines + Huffman-coded strings.
  The dynamic table is advertised at capacity 0 (encoder inserts nothing; decoder rejects
  peer insertions as a protocol error). Verified against the RFC 7541 Huffman vector and
  round-trip tests.
- **Response trailers**: `ctx.Response.AddTrailer(...)` works on h3 (trailing HEADERS
  after the body) — gRPC over h3 uses this.
- **WebSocket over h3 (RFC 9220)**: not yet wired — WebTransport is explicitly out of scope.

## Interop

CI runs `http3.yml` (ubuntu): installs `libmsquic`, then `curl --http3` and aioquic's
`http3_client.py` against the TLS + h3 sample, asserting 200s and round-tripped headers.

## See also

- [hosting-and-aot.md](hosting-and-aot.md)
- [middleware.md](middleware.md)
- [grpc.md](grpc.md)
