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
        var form = MultipartFormParser.Parse(bytes, $"multipart/form-data; boundary={boundary}", 1024, 5);
        Assert.Equal("hello", form.GetField("title"));
        Assert.Single(form.Files);
        Assert.Equal("a.txt", form.Files[0].FileName);
        Assert.Equal("abc", Encoding.UTF8.GetString(form.Files[0].ToArray()));
    }
}
