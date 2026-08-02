namespace Elsie.Web.Http;

/// <summary>Request body that may need draining before HTTP/1.1 keep-alive reuse.</summary>
internal interface IDrainableRequestBody
{
    bool IsFullyConsumed { get; }

    Task DrainAsync(CancellationToken cancellationToken = default);
}
