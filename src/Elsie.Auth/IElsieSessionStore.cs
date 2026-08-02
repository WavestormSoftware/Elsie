namespace Elsie.Auth;

/// <summary>
/// Server-side session storage for cookie authentication. When configured on
/// <see cref="ElsieAuthOptions.SessionStore"/>, cookies carry an opaque v2 id
/// (≥128-bit) and the principal is stored here instead of in the client-side ticket.
/// </summary>
public interface IElsieSessionStore
{
    /// <summary>Stores <paramref name="payload"/> for <paramref name="sessionId"/> with a sliding TTL.</summary>
    Task SetAsync(string sessionId, byte[] payload, TimeSpan slidingTtl, CancellationToken cancellationToken = default);

    /// <summary>Reads the payload for <paramref name="sessionId"/>; null when missing or expired.</summary>
    Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Removes the session (sign-out).</summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken = default);
}
