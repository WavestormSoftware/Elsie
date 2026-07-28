namespace Elsie;

/// <summary>
/// Outcome of binding a request value (JSON body, etc.).
/// </summary>
public readonly struct ElsieBindResult<T>
{
    private ElsieBindResult(T? value, ElsieResult? error, bool isSuccess)
    {
        Value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    public bool IsSuccess { get; }
    public T? Value { get; }
    public ElsieResult? Error { get; }

    public static ElsieBindResult<T> Success(T value) => new(value, error: null, isSuccess: true);

    public static ElsieBindResult<T> Fail(ElsieResult error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error, isSuccess: false);
    }
}
