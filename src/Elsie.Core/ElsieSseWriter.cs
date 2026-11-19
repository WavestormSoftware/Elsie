using System.Globalization;
using System.Text;

namespace Elsie;

/// <summary>A Server-Sent Event: optional id, event name, retry hint and multi-line data.</summary>
public readonly record struct ElsieSseEvent(string? Data, string? EventName = null, string? Id = null, int? RetryMs = null)
{
    /// <summary>Data-only event.</summary>
    public static ElsieSseEvent FromData(string? data) => new(data);
}

/// <summary>Helpers for Server-Sent Events requests.</summary>
public static class ElsieSse
{
    /// <summary>
    /// The client's last received event id: the <c>Last-Event-ID</c> header, or a
    /// <c>lastEventId</c> query value for clients that cannot set headers (EventSource polyfills).
    /// </summary>
    public static string? GetLastEventId(ElsieRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var header = request.GetHeader("Last-Event-ID");
        if (!string.IsNullOrEmpty(header))
        {
            return header;
        }

        var query = request.GetQuery("lastEventId");
        return string.IsNullOrEmpty(query) ? null : query;
    }

    /// <summary>True when the request accepts <c>text/event-stream</c> (EventSource always does).</summary>
    public static bool AcceptsEventStream(ElsieRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var accept = request.GetHeader("Accept");
        return string.IsNullOrEmpty(accept)
            || accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.Ordinal);
    }
}

/// <summary>Writes Server-Sent Events to an underlying stream.</summary>
public sealed class ElsieSseWriter
{
    private readonly Stream _stream;
    private readonly StreamWriter _writer;

    public ElsieSseWriter(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true)
        {
            NewLine = "\n",
            AutoFlush = true
        };
    }

    /// <summary>Write a comment line (also used as a keep-alive heartbeat).</summary>
    public Task WriteCommentAsync(string comment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return WriteRawAsync($": {comment}\n\n", cancellationToken);
    }

    /// <summary>Write a heartbeat comment — keeps proxies from closing an idle event stream.</summary>
    public Task WriteHeartbeatAsync(CancellationToken cancellationToken = default) =>
        WriteCommentAsync("heartbeat", cancellationToken);

    /// <summary>Send the client's reconnect delay (applies to the next reconnection).</summary>
    public Task WriteRetryAsync(int retryMs, CancellationToken cancellationToken = default)
    {
        if (retryMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryMs));
        }

        return WriteRawAsync($"retry: {retryMs.ToString(CultureInfo.InvariantCulture)}\n\n", cancellationToken);
    }

    public Task WriteEventAsync(ElsieSseEvent sseEvent, CancellationToken cancellationToken = default) =>
        WriteEventAsync(
            sseEvent.Data,
            sseEvent.EventName,
            sseEvent.Id,
            sseEvent.RetryMs,
            cancellationToken);

    public Task WriteEventAsync(
        string? data,
        string? eventName = null,
        string? id = null,
        int? retryMs = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        if (id is not null)
        {
            sb.Append("id: ").Append(id).Append('\n');
        }

        if (eventName is not null)
        {
            sb.Append("event: ").Append(eventName).Append('\n');
        }

        if (retryMs is not null)
        {
            sb.Append("retry: ").Append(retryMs.Value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        if (data is null)
        {
            sb.Append("data: \n");
        }
        else
        {
            foreach (var line in data.Split('\n'))
            {
                sb.Append("data: ").Append(line).Append('\n');
            }
        }

        sb.Append('\n');
        return WriteRawAsync(sb.ToString(), cancellationToken);
    }

    public async Task WriteRawAsync(string payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await _writer.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
