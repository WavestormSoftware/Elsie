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
    /// <summary>Attaches a principal to the request (may perform async lookups, e.g. JWKS / sessions).</summary>
    Task AttachAsync(ElsieRequest request, CancellationToken cancellationToken);
}
