namespace LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;

/// <summary>
/// The path of a range request is the first five characters of a password's SHA-1. Have I Been Pwned
/// cannot tie it to anyone, but a trace would store it next to the request and the user it belongs to,
/// and that cuts a cracking dictionary about a million times. So the trace pipeline drops these
/// requests (ADR-0065).
/// </summary>
public static class PwnedPasswordsTelemetry
{
    public static bool IsRangeApiRequest(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.RequestUri is { IsAbsoluteUri: true } requestUri
               && string.Equals(
                   requestUri.Host,
                   PwnedPasswordsDependencyInjection.BaseAddress.Host,
                   StringComparison.OrdinalIgnoreCase);
    }
}
