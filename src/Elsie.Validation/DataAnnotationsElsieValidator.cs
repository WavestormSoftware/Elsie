using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.DependencyInjection;

namespace Elsie.Validation;

/// <summary>Validates with <see cref="Validator"/> / DataAnnotations attributes.</summary>
public sealed class DataAnnotationsElsieValidator<T> : IElsieValidator<T>
{
    public IReadOnlyDictionary<string, string[]> Validate(T value)
    {
        if (value is null)
        {
            return new Dictionary<string, string[]> { [""] = ["Value is required."] };
        }

        var ctx = new ValidationContext(value);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(value, ctx, results, validateAllProperties: true))
        {
            return new Dictionary<string, string[]>(0);
        }

        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            var key = r.MemberNames.FirstOrDefault() ?? "";
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(r.ErrorMessage ?? "Invalid.");
        }

        return map.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }
}

public static class ElsieValidationServiceExtensions
{
    public static IServiceCollection AddElsieDataAnnotationsValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(typeof(IElsieValidator<>), typeof(DataAnnotationsElsieValidator<>));
        return services;
    }
}

public static class ElsieValidationContextExtensions
{
    public static ElsieResult? ValidateWithDataAnnotations<T>(this ElsieContext ctx, T value, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var validator = ctx.GetService<IElsieValidator<T>>() ?? new DataAnnotationsElsieValidator<T>();
        return ElsieValidation.Validate(value, validator, detail);
    }
}
