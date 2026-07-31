namespace Elsie.Validation;

/// <summary>Optional validation seam. Implement in app code or use <c>Elsie.Validation</c> DataAnnotations adapter.</summary>
public interface IElsieValidator<in T>
{
    /// <summary>Validate <paramref name="value"/>. Empty errors = success.</summary>
    IReadOnlyDictionary<string, string[]> Validate(T value);
}

/// <summary>Helpers to turn validator output into problem results.</summary>
public static class ElsieValidation
{
    public static ElsieResult? ToProblem(
        IReadOnlyDictionary<string, string[]> errors,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            return null;
        }

        return ElsieResult.ValidationProblem(errors, detail);
    }

    public static ElsieResult? Validate<T>(T value, IElsieValidator<T> validator, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(validator);
        return ToProblem(validator.Validate(value), detail);
    }
}
