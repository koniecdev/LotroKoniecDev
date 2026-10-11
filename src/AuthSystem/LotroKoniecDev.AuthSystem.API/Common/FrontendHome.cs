using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// The website's home page, where the logo on every account page leads (#1074). Every page gets it through
/// <c>Pages/_ViewImports.cshtml</c>, so its logo only needs <c>href="@FrontendHome.Url"</c> and no code in
/// the page model. A fixed address would send a staging tester or a local developer to production.
/// </summary>
internal sealed class FrontendHome
{
    /// <summary>
    /// Null when the web client is not configured, for example on a bare test host. Razor then leaves the
    /// <c>href</c> out, and the logo shows without a link.
    /// </summary>
    public string? Url { get; }

    public FrontendHome(IOptions<OpenIddictSettings> openIddictSettings)
    {
        ArgumentNullException.ThrowIfNull(openIddictSettings);

        Url = FrontendUrl.For(openIddictSettings.Value.WebClient, "/");
    }
}
