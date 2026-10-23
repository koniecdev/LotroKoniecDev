namespace LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;

/// <summary>
/// When an API refuses a call (a 401, or the anonymous discovery links), a marker is set under the access
/// token that call carried, and the next cookie validation signs the user out. The detour exists because
/// a <c>[StreamRendering]</c> page may already have sent its headers. The marker names the token, not the
/// user, so the same user's session on another device goes on (#1061).
/// </summary>
internal interface IDeadSessionRegistry
{
    ValueTask MarkDeadAsync(string accessToken, CancellationToken cancellationToken = default);

    /// <summary>Clears the marker in the same call, so the sign-out happens only once.</summary>
    ValueTask<bool> ConsumeAsync(string accessToken, CancellationToken cancellationToken = default);
}
