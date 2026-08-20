using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Builds absolute frontend URLs from the web client's app root, which is its first post-logout
/// redirect URI. The auth pages can then link and redirect to the application without a setting of
/// their own for the frontend origin.
/// The two contexts share no code, so the paths passed in here copy frontend routes by hand, and
/// renaming a route there has to be repeated at the call site.
/// </summary>
/// <remarks>
/// The scheme check matters. On Unix a bare path such as <c>"/app"</c> parses as an absolute
/// <c>file://</c> URI, so a wrongly configured app root would produce a <c>file:///…</c> target.
/// </remarks>
internal static class FrontendUrl
{
    internal static string? For(WebClientSettings webClient, string path) =>
        webClient.PostLogoutRedirectUris is [string appRoot, ..]
        && Uri.TryCreate(appRoot, UriKind.Absolute, out Uri? appRootUri)
        && (string.Equals(appRootUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || string.Equals(appRootUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            ? new Uri(appRootUri, path).ToString()
            : null;
}
