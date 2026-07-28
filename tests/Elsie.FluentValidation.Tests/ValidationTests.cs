using Elsie.FluentValidation;
using Elsie.Testing;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elsie.FluentValidation.Tests;

public sealed record CreateTodo(string Title);

// Public so AddValidatorsFromAssembly can discover it.
public sealed class CreateTodoValidator : AbstractValidator<CreateTodo>
{
    public CreateTodoValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MinimumLength(3);
    }
}

public class ValidationTests
{
    private sealed class TodosModule : ElsieModule
    {
        public TodosModule()
        {
            Post("/todos", async (ctx, ct) =>
            {
                var bind = await ctx.BindAndValidateJsonAsync<CreateTodo>(cancellationToken: ct);
                if (!bind.IsSuccess)
                {
                    return bind.Error!;
                }

                return ctx.Json(bind.Value, 201);
            });
        }
    }

    [Fact]
    public async Task BindAndValidate_rejects_invalid_body()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            s.AddElsieModule<TodosModule>();
            s.AddSingleton<IValidator<CreateTodo>, CreateTodoValidator>();
        });

        var response = await host.PostJsonAsync("/todos", new CreateTodo("x"));
        Assert.Equal(400, response.StatusCode);
        Assert.Contains("errors", response.ReadAsString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddElsieFluentValidation_registers_assembly_validators()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            s.AddElsieModule<TodosModule>();
            s.AddElsieFluentValidation(typeof(CreateTodoValidator).Assembly);
        });

        var response = await host.PostJsonAsync("/todos", new CreateTodo("x"));
        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task BindAndValidate_accepts_valid_body()
    {
        await using var host = ElsieInMemoryHost.Create(s =>
        {
            s.AddElsieModule<TodosModule>();
            s.AddSingleton<IValidator<CreateTodo>, CreateTodoValidator>();
        });

        var response = await host.PostJsonAsync("/todos", new CreateTodo("ship it"));
        Assert.Equal(201, response.StatusCode);
        Assert.Contains("ship it", response.ReadAsString(), StringComparison.Ordinal);
    }
}
