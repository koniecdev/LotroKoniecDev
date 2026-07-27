using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Builds absolute frontend URLs from the web client's app root — its first post-logout redirect URI —
/// so the auth pages can link and redirect to the application without a frontend-origin setting of
/// their own. The two contexts share no code, so the paths passed in mirror frontend routes and a
/// rename there has to be repeated at the call site.
/// </summary>
/// <remarks>
/// The scheme screen is load-bearing: on Unix a bare path such as <c>"/app"</c> parses as an absolute
/// <c>file://</c> URI, so a misconfigured app root would otherwise yield a <c>file:///…</c> target.
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
