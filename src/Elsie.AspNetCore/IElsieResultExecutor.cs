using Microsoft.AspNetCore.Http;

namespace Elsie.AspNetCore;

public interface IElsieResultExecutor
{
    Task ExecuteAsync(HttpContext httpContext, ElsieResult result, CancellationToken cancellationToken);
}
