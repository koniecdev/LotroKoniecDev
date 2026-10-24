using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Common;

/// <summary>
/// Where the logo on every account page leads (#1074), injected through <c>Pages/_ViewImports.cshtml</c>.
/// A fixed address would send a staging tester or a local developer to production.
/// </summary>
internal sealed class FrontendHome
{
    /// <summary>
    /// Null when the web client is not configured, for example on a bare test host. Razor then leaves the
    /// <c>href</c> out.
    /// </summary>
    public string? Url { get; }

    public FrontendHome(IOptions<OpenIddictSettings> openIddictSettings)
    {
        ArgumentNullException.ThrowIfNull(openIddictSettings);

        Url = FrontendUrl.For(openIddictSettings.Value.WebClient, "/");
    }
}
