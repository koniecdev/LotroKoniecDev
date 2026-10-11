namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

/// <summary>
/// A per-session "this token is dead" marker that survives between requests. When an API refuses a call,
/// with a 401 or with the anonymous discovery links, a marker is set under the access token that call
/// carried. The cookie's <c>OnValidatePrincipal</c> hook reads it on the next request, where the response
/// has not started yet, and signs the user out properly.
/// This detour is what lets a 401 seen in the middle of a response still end the session cleanly on the
/// next request: SSR pages use <c>[StreamRendering]</c>, so the headers may already have been sent.
/// The marker names the token, not the user: the same user may be signed in on another device, with a
/// token that still works, and that session must go on (#1061).
/// </summary>
internal interface IDeadSessionRegistry
{
    /// <summary>Marks the session that holds this access token dead, so its next cookie validation signs it out.</summary>
    ValueTask MarkDeadAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> once when the session that holds this access token was marked dead, and
    /// clears the marker in the same call, so the sign-out happens only once.
    /// </summary>
    ValueTask<bool> ConsumeAsync(string accessToken, CancellationToken cancellationToken = default);
}
