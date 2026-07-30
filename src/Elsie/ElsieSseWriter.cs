using System.Globalization;
using System.Text;

namespace Elsie;

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

    public Task WriteCommentAsync(string comment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(comment);
        return WriteRawAsync($": {comment}\n\n", cancellationToken);
    }

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
