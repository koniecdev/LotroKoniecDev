namespace LotroKoniecDev.Frontend.Infrastructure.HttpClients;

internal static class UriOrigin
{
    /// <summary>
    /// Scheme, host and port, as a browser compares origins. A default port and the same port written out
    /// count as one.
    /// </summary>
    public static bool AreSame(Uri first, Uri second) =>
        string.Equals(first.Scheme, second.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;
}
