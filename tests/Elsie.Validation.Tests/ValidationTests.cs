using System.ComponentModel.DataAnnotations;
using Elsie.Validation;
using Xunit;

namespace Elsie.Validation.Tests;

public class ValidationTests
{
    private sealed class Model
    {
        [Required]
        public string? Name { get; set; }
    }

    [Fact]
    public void DataAnnotations_fail_required()
    {
        var v = new DataAnnotationsElsieValidator<Model>();
        var errors = v.Validate(new Model());
        Assert.NotEmpty(errors);
        Assert.NotNull(ElsieValidation.ToProblem(errors));
    }

    [Fact]
    public void DataAnnotations_ok()
    {
        var v = new DataAnnotationsElsieValidator<Model>();
        var errors = v.Validate(new Model { Name = "Ada" });
        Assert.Empty(errors);
    }
}
