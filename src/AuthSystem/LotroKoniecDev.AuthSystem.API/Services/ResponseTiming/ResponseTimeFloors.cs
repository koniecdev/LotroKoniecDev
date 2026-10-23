namespace LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;

/// <summary>
/// Constants, not settings: a setting would only add a way to switch the guard off in production
/// (ADR-0059 §3).
/// </summary>
internal static class ResponseTimeFloors
{
    /// <summary>
    /// About two and a half times the slowest normal branch: one PBKDF2 hash and up to four database round
    /// trips.
    /// </summary>
    internal static readonly TimeSpan AccountLookup = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Creating an account takes about 17 database round trips, about 170 ms from the staging box
    /// (ADR-0059 amendment, decision 10). The hash and the breach check run before the clock starts.
    /// </summary>
    internal static readonly TimeSpan Registration = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Resend confirmation sends inside the request (ADR-0038 decision 4): a new SMTP connection, TLS,
    /// login and send, every time.
    /// </summary>
    internal static readonly TimeSpan LiveMailSend = TimeSpan.FromSeconds(3);
}
