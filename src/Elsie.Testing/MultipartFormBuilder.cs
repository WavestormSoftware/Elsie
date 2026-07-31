using System.Net.Http.Headers;
using System.Text;

namespace Elsie.Testing;

/// <summary>Builds multipart form content for HTTP client tests.</summary>
public sealed class MultipartFormBuilder
{
    private readonly MultipartFormDataContent _content = new();

    public MultipartFormBuilder AddField(string name, string value)
    {
        _content.Add(new StringContent(value, Encoding.UTF8), name);
        return this;
    }

    public MultipartFormBuilder AddFile(
        string name,
        string fileName,
        byte[] bytes,
        string contentType = "application/octet-stream")
    {
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        _content.Add(part, name, fileName);
        return this;
    }

    public MultipartFormBuilder AddFile(
        string name,
        string fileName,
        Stream stream,
        string contentType = "application/octet-stream")
    {
        var part = new StreamContent(stream);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        _content.Add(part, name, fileName);
        return this;
    }

    public MultipartFormDataContent Build() => _content;
}
