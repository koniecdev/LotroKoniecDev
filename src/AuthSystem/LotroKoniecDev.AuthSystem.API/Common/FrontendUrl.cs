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
/// <see cref="AbsoluteHttpUri"/> keeps a wrongly configured app root such as <c>"/app"</c> from
/// producing a <c>file:///…</c> target.
/// </remarks>
internal static class FrontendUrl
{
    internal static string? For(WebClientSettings webClient, string path) =>
        webClient.PostLogoutRedirectUris is [string appRoot, ..]
        && AbsoluteHttpUri.TryParse(appRoot, out Uri? appRootUri)
            ? new Uri(appRootUri, path).ToString()
            : null;
}
