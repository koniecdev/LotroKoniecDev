namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

/// <summary>
/// The one rule for a translation-server address, shared by the CLI's sync, the discovered link and the
/// desktop app's settings. Over plain http anyone on the path can change the downloaded file (#391), so
/// only loopback, with no network hop, may skip TLS.
/// </summary>
public static class TranslationServerAddress
{
    public static bool IsAllowed(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && HasAllowedScheme(uri);

    public static bool HasAllowedScheme(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}
