namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

/// <summary>
/// A per-session "this token is dead" marker that survives between requests. When the TMS delegating
/// handler sees a 401, it sets a marker under the authenticated subject. The cookie's
/// <c>OnValidatePrincipal</c> hook reads it on the next request, where the response has not started
/// yet, and signs the user out properly.
/// This detour is what lets a 401 seen in the middle of a response still end the session cleanly on the
/// next request: SSR pages use <c>[StreamRendering]</c>, so the headers may already have been sent.
/// </summary>
internal interface IDeadSessionRegistry
{
    /// <summary>Marks the subject's session dead, so the next cookie validation signs it out.</summary>
    ValueTask MarkDeadAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> once when the subject was marked dead, and clears the marker in
    /// the same call, so the sign-out happens only once.
    /// </summary>
    ValueTask<bool> ConsumeAsync(string subject, CancellationToken cancellationToken = default);
}
