namespace Elsie;

/// <summary>
/// A response header name or value was rejected because it contains control characters
/// (CR / LF / NUL) that could inject header lines. Subclasses <see cref="ArgumentException"/>
/// so existing callers that treat the rejection as an argument error keep working, while
/// the dispatcher can specifically map it to a 400 response instead of a 500.
/// </summary>
public sealed class ElsieHeaderValidationException : ArgumentException
{
    public ElsieHeaderValidationException(string message, string paramName)
        : base(message, paramName)
    {
    }
}
