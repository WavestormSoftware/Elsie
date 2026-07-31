namespace Elsie.Web.Http;

internal sealed class ParsedHttpRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string QueryString { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> QueryValues { get; init; }
    public required string Protocol { get; init; }
    public required Dictionary<string, List<string>> Headers { get; init; }
    public required Stream Body { get; init; }
    public long? ContentLength { get; init; }
    public string? ContentType { get; init; }
    public bool KeepAlive { get; init; }
}
