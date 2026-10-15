namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// How long each answer that hides whether an address has an account takes at least (ADR-0059 §3). They
/// are constants and not settings: a setting would only add a way to switch the guard off in production.
/// </summary>
internal static class ResponseTimeFloors
{
    /// <summary>
    /// About two and a half times the slowest normal branch: one PBKDF2 hash and up to four database round
    /// trips.
    /// </summary>
    internal static readonly TimeSpan AccountLookup = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Creating an account takes about a dozen database round trips (the two lookups, the reservation
    /// check, Identity's own lookups before the insert and before the role, the inserts and the commit),
    /// three times what <see cref="AccountLookup"/> was sized for (#860). The password hash runs before the
    /// clock starts, so it is not part of it.
    /// </summary>
    internal static readonly TimeSpan Registration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Resend confirmation sends the mail inside the request (ADR-0038 decision 4): a new SMTP connection,
    /// TLS, login and send, every time.
    /// </summary>
    internal static readonly TimeSpan LiveMailSend = TimeSpan.FromSeconds(3);
}
