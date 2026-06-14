namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

/// <summary>
/// Durable, per-session "dead token" marker. The reactive 401 net (the TMS delegating handler) raises
/// a marker keyed by the authenticated subject; the cookie <c>OnValidatePrincipal</c> hook reads it on
/// the next request — where the response has not started streaming yet — and performs the clean
/// <c>SignOutAsync</c>. This indirection is what lets a 401 observed mid-stream (SSR responses use
/// <c>[StreamRendering]</c>, so the response headers may already be flushed) still end the session
/// cleanly on the very next request.
/// </summary>
internal interface IDeadSessionRegistry
{
    /// <summary>Marks the subject's session dead so the next principal validation signs it out.</summary>
    ValueTask MarkDeadAsync(string subject, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <see langword="true"/> exactly once if the subject was marked dead, clearing the
    /// marker in the same call so the sign-out is performed only once.
    /// </summary>
    ValueTask<bool> ConsumeAsync(string subject, CancellationToken cancellationToken = default);
}
