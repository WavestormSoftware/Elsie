using System.Net;
using System.Net.Sockets;
using System.Text;
using Elsie.Soak.Soak;

namespace Elsie.Soak.Clients;

/// <summary>A raw HTTP/1.1 keep-alive client over <see cref="TcpClient"/>.</summary>
internal sealed class RawH1Client : IAsyncDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly byte[] _buffer = new byte[BufferSize];
    private int _pos; // start of unread buffered bytes
    private int _len; // end of valid buffered bytes
    private bool _remoteClosed;

    private RawH1Client(TcpClient tcp, NetworkStream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public bool RemoteClosed => _remoteClosed;

    public static async Task<RawH1Client> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await tcp.ConnectAsync(endpoint.Address, endpoint.Port, ct).ConfigureAwait(false);
            tcp.NoDelay = true;
            return new RawH1Client(tcp, tcp.GetStream());
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Sends one request and reads the complete response (keeps the connection open).</summary>
    public async Task<H1Response> SendAsync(
        string method,
        string path,
        ReadOnlyMemory<byte> body = default,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder(256);
        sb.Append(method).Append(' ').Append(path).Append(" HTTP/1.1\r\n");
        sb.Append("Host: 127.0.0.1\r\nConnection: keep-alive\r\n");
        if (body.Length > 0)
        {
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        sb.Append("\r\n");
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await _stream.WriteAsync(body, ct).ConfigureAwait(false);
        }

        await _stream.FlushAsync(ct).ConfigureAwait(false);
        return await ReadResponseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes request headers and part of the body, then half-closes the write side — a
    /// graceful client close mid-pipeline (the server must not hang or leak the connection).
    /// </summary>
    public async Task AbortMidRequestAsync(CancellationToken ct)
    {
        const string headers = "POST /echo HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: keep-alive\r\nContent-Length: 1048576\r\n\r\n";
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct).ConfigureAwait(false);
        var chunk = new byte[8192];
        Array.Fill(chunk, (byte)0xAB);
        await _stream.WriteAsync(chunk, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        _tcp.Client.Shutdown(SocketShutdown.Send);
    }

    private async Task<H1Response> ReadResponseAsync(CancellationToken ct)
    {
        var statusLine = await ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(statusLine))
        {
            _remoteClosed = true;
            throw new IOException("Connection closed before a response was received.");
        }

        var parts = statusLine.Split(' ', 3);
        var status = parts.Length >= 2 && int.TryParse(parts[1], out var s) ? s : 0;

        var contentLength = 0;
        var keepAlive = true;
        while (await ReadLineAsync(ct).ConfigureAwait(false) is { Length: > 0 } headerLine)
        {
            var colon = headerLine.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = headerLine[..colon].Trim();
            var value = headerLine[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(value, out contentLength);
            }
            else if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                keepAlive = !value.Contains("close", StringComparison.OrdinalIgnoreCase);
            }
        }

        var body = contentLength > 0 ? new byte[contentLength] : [];
        var read = 0;
        while (read < contentLength)
        {
            // Satisfy from the buffer first.
            var available = _len - _pos;
            if (available > 0)
            {
                var take = Math.Min(available, contentLength - read);
                _buffer.AsSpan(_pos, take).CopyTo(body.AsSpan(read, take));
                _pos += take;
                read += take;
                continue;
            }

            if (!await FillBufferAsync(ct).ConfigureAwait(false))
            {
                _remoteClosed = true;
                throw new IOException($"Connection closed with {contentLength - read} body bytes outstanding.");
            }
        }

        return new H1Response(status, body, keepAlive);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            for (var i = _pos; i < _len; i++)
            {
                if (_buffer[i] != (byte)'\n')
                {
                    continue;
                }

                var line = Encoding.ASCII.GetString(_buffer, _pos, i - _pos).TrimEnd('\r');
                _pos = i + 1;
                return line;
            }

            if (!await FillBufferAsync(ct).ConfigureAwait(false))
            {
                _remoteClosed = true;
                return null;
            }
        }
    }

    /// <summary>Reads more bytes into the buffer (compacting when full). Returns false on EOF.</summary>
    private async Task<bool> FillBufferAsync(CancellationToken ct)
    {
        if (_len == _buffer.Length)
        {
            Buffer.BlockCopy(_buffer, _pos, _buffer, 0, _len - _pos);
            _len -= _pos;
            _pos = 0;
        }

        var n = await _stream.ReadAsync(_buffer.AsMemory(_len, _buffer.Length - _len), ct).ConfigureAwait(false);
        _len += n;
        return n > 0;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _stream.Dispose();
        }
        catch
        {
            // ignore
        }

        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A parsed HTTP/1.1 response.</summary>
internal readonly record struct H1Response(int StatusCode, byte[] Body, bool KeepAlive)
{
    public string BodyAsText => System.Text.Encoding.UTF8.GetString(Body);
}