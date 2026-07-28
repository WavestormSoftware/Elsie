using Microsoft.AspNetCore.Http;

namespace Elsie;

public interface IElsieResultExecutor
{
    Task ExecuteAsync(HttpContext httpContext, ElsieResult result, CancellationToken cancellationToken);
}
