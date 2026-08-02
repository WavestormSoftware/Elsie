namespace Elsie;

/// <summary>
/// Protocol-level request failure (body idle timeout, payload too large, truncated body).
/// Mapped to an HTTP problem response by the dispatcher.
/// </summary>
public sealed class ElsieRequestException : Exception
{
    public ElsieRequestException(int statusCode, string message)
        : base(message)
    {
        if (statusCode < 400 || statusCode > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "Expected 4xx/5xx.");
        }

        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    public string Title => StatusCode switch
    {
        408 => "Request Timeout",
        413 => "Payload Too Large",
        400 => "Bad Request",
        _ => "Request Error"
    };
}
