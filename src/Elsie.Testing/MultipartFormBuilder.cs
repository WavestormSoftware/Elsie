using System.Net.Http.Headers;
using System.Text;

namespace Elsie.Testing;

/// <summary>
/// Builds <see cref="MultipartFormDataContent"/> for ASP.NET TestServer tests.
/// Core does not parse multipart — use this with <see cref="ElsieTestHost"/> / HttpClient.
/// </summary>
public sealed class MultipartFormBuilder
{
    private readonly MultipartFormDataContent _content = new();

    public MultipartFormBuilder AddField(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _content.Add(new StringContent(value, Encoding.UTF8), name);
        return this;
    }

    public MultipartFormBuilder AddFile(
        string name,
        byte[] bytes,
        string fileName,
        string contentType = "application/octet-stream")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        _content.Add(file, name, fileName);
        return this;
    }

    public MultipartFormBuilder AddFile(
        string name,
        Stream stream,
        string fileName,
        string contentType = "application/octet-stream")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        _content.Add(file, name, fileName);
        return this;
    }

    public MultipartFormDataContent Build() => _content;
}
