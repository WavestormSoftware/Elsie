namespace Elsie;

public enum ElsieDispatchStatus
{
    Handled = 0,
    NotFound = 1,
    MethodNotAllowed = 2
}

/// <summary>
/// Outcome of dispatching a request through Elsie routing and pipelines.
/// </summary>
public sealed class ElsieDispatchResult
{
    private ElsieDispatchResult(
        ElsieDispatchStatus status,
        ElsieResult? result,
        ElsieResponse? response,
        IReadOnlyList<string>? allowedMethods)
    {
        Status = status;
        Result = result;
        Response = response;
        AllowedMethods = allowedMethods ?? Array.Empty<string>();
    }

    public ElsieDispatchStatus Status { get; }
    public ElsieResult? Result { get; }
    public ElsieResponse? Response { get; }
    public IReadOnlyList<string> AllowedMethods { get; }

    public static ElsieDispatchResult Handled(ElsieResult result, ElsieResponse response)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(response);
        return new(ElsieDispatchStatus.Handled, result, response, allowedMethods: null);
    }

    public static ElsieDispatchResult NotFound() =>
        new(ElsieDispatchStatus.NotFound, result: null, response: null, allowedMethods: null);

    public static ElsieDispatchResult MethodNotAllowed(IReadOnlyList<string> allowedMethods)
    {
        ArgumentNullException.ThrowIfNull(allowedMethods);
        return new(ElsieDispatchStatus.MethodNotAllowed, result: null, response: null, allowedMethods);
    }
}
