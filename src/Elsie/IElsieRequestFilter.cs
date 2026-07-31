namespace Elsie.Web;

/// <summary>
/// Optional pre-dispatch hook (CORS preflight, etc.). First non-null response wins.
/// </summary>
public interface IElsieRequestFilter
{
    Task<ElsieHttpResponse?> TryHandleAsync(ElsieRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Optional principal attachment before dispatch (cookie/JWT auth packages).
/// </summary>
public interface IElsiePrincipalAttacher
{
    void Attach(ElsieRequest request);
}
