using FluentValidation;

namespace Elsie.FluentValidation;

/// <summary>Bind JSON and run FluentValidation in one step.</summary>
public static class ElsieFluentValidationExtensions
{
    /// <summary>
    /// Deserialize JSON then validate with <paramref name="validator"/> or DI <see cref="IValidator{T}"/>.
    /// When no validator is registered, returns the bind result unchanged.
    /// Register validators yourself (<c>services.AddValidatorsFromAssembly(...)</c> or <c>AddSingleton&lt;IValidator&lt;T&gt;, ...&gt;</c>).
    /// </summary>
    public static async Task<ElsieBindResult<T>> BindAndValidateJsonAsync<T>(
        this ElsieContext context,
        IValidator<T>? validator = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bind = await context.BindJsonAsync<T>(cancellationToken).ConfigureAwait(false);
        if (!bind.IsSuccess)
        {
            return bind;
        }

        validator ??= context.GetService<IValidator<T>>();
        if (validator is null)
        {
            return bind;
        }

        var result = await validator.ValidateAsync(bind.Value!, cancellationToken).ConfigureAwait(false);
        if (result.IsValid)
        {
            return bind;
        }

        var errors = result.Errors
            .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? string.Empty : e.PropertyName, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => e.ErrorMessage).ToArray(),
                StringComparer.Ordinal);

        return ElsieBindResult<T>.Fail(ElsieResult.ValidationProblem(errors));
    }
}
