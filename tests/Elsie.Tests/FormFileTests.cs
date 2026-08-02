using System.Text;
using Elsie.Binding;
using Xunit;

namespace Elsie.Tests;

public class FormFileTests
{
    [Fact]
    public void Multipart_parses_file_and_field()
    {
        var boundary = "----bound";
        var sb = new StringBuilder();
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"title\"\r\n\r\n");
        sb.Append("hello\r\n");
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\n");
        sb.Append("Content-Type: text/plain\r\n\r\n");
        sb.Append("abc\r\n");
        sb.Append("--").Append(boundary).Append("--\r\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        using var form = MultipartFormParser.Parse(bytes, $"multipart/form-data; boundary={boundary}", 1024, 5);
        Assert.Equal("hello", form.GetField("title"));
        Assert.Single(form.Files);
        Assert.Equal("a.txt", form.Files[0].FileName);
        Assert.False(form.Files[0].IsFileBacked);
        Assert.Equal("abc", Encoding.UTF8.GetString(form.Files[0].ToArray()));
    }

    [Fact]
    public void Large_file_part_spills_to_temp_file_and_deletes_on_dispose()
    {
        var boundary = "----spill";
        var payload = new string('Z', 5 * 1024 * 1024); // 5 MiB
        var sb = new StringBuilder();
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"file\"; filename=\"big.bin\"\r\n");
        sb.Append("Content-Type: application/octet-stream\r\n\r\n");
        sb.Append(payload).Append("\r\n");
        sb.Append("--").Append(boundary).Append("--\r\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());

        string? tempPath;
        using (var form = MultipartFormParser.Parse(
                   bytes,
                   $"multipart/form-data; boundary={boundary}",
                   maxFileBytes: 20 * 1024 * 1024,
                   maxFiles: 5,
                   memoryThresholdBytes: 1 * 1024 * 1024))
        {
            Assert.Single(form.Files);
            var file = form.Files[0];
            Assert.True(file.IsFileBacked);
            tempPath = file.TempPath;
            Assert.False(string.IsNullOrEmpty(tempPath));
            Assert.True(File.Exists(tempPath!));
            Assert.Equal(payload.Length, file.Length);
            Assert.Equal(payload, Encoding.UTF8.GetString(file.ToArray()));
        }

        Assert.False(File.Exists(tempPath!));
    }

    [Fact]
    public void MaxFormFiles_still_enforced()
    {
        var boundary = "----many";
        var sb = new StringBuilder();
        for (var i = 0; i < 3; i++)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append($"Content-Disposition: form-data; name=\"f\"; filename=\"{i}.txt\"\r\n\r\n");
            sb.Append("x\r\n");
        }

        sb.Append("--").Append(boundary).Append("--\r\n");
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MultipartFormParser.Parse(bytes, $"multipart/form-data; boundary={boundary}", 1024, maxFiles: 2));
        Assert.Contains("Too many", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_stream_path_works()
    {
        var boundary = "----stream";
        var sb = new StringBuilder();
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Disposition: form-data; name=\"file\"; filename=\"s.txt\"\r\n\r\n");
        sb.Append("streamed\r\n");
        sb.Append("--").Append(boundary).Append("--\r\n");
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(sb.ToString()));
        await using var form = await MultipartFormParser.ParseAsync(
            ms,
            $"multipart/form-data; boundary={boundary}",
            maxFileBytes: 1024,
            maxFiles: 5);
        Assert.Equal("streamed", Encoding.UTF8.GetString(form.Files[0].ToArray()));
    }
}
