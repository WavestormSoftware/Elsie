using System.Net.Sockets;
using System.Text;
using Xunit;

namespace Elsie.Web.Tests;

public class UnixSocketTests
{
    private sealed class PingModule : ElsieModule
    {
        public PingModule() => Get("/ping", () => ElsieResult.Text("pong"));
    }

    [Fact]
    public async Task Unix_domain_socket_serves_http11()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            return; // skip on platforms without UDS
        }

        var path = Path.Combine(Path.GetTempPath(), "elsie-uds-" + Guid.NewGuid().ToString("n") + ".sock");
        try
        {
            await using var server = await ElsieApp.Create()
                .QuietConsole(false)
                .Listen($"http+unix://{path}")
                .Configure(o => o.ScanEntryAssembly = false)
                .Module<PingModule>()
                .StartAsync();

            Assert.Contains(path, server.UnixSocketPaths);

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(server.UnixSocketPaths[0]));
            await using var ns = new NetworkStream(socket, ownsSocket: false);

            await ns.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /ping HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"));

            using var reader = new StreamReader(ns, Encoding.ASCII);
            var response = await reader.ReadToEndAsync();
            Assert.Contains("200", response, StringComparison.Ordinal);
            Assert.Contains("pong", response, StringComparison.Ordinal);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }
    }
}
