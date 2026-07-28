namespace Elsie.Views;

public interface IElsieViewEngine
{
    Task<string> RenderAsync(string viewName, object? model, CancellationToken cancellationToken = default);
}
