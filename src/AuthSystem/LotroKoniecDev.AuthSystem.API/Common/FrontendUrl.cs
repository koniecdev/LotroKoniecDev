using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Builds frontend URLs from the web client's first post-logout redirect URI, so the auth pages need no
/// setting of their own for the frontend origin. The paths passed in copy frontend routes by hand, so a
/// route renamed there has to be renamed at the call site too.
/// </summary>
internal static class FrontendUrl
{
    internal static string? For(WebClientSettings webClient, string path) =>
        webClient.PostLogoutRedirectUris is [string appRoot, ..]
        && AbsoluteHttpUri.TryParse(appRoot, out Uri? appRootUri)
            ? new Uri(appRootUri, path).ToString()
            : null;
}
