using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.DependencyInjection;
using TermsComponent = LotroKoniecDev.Frontend.Components.Pages.Terms.Terms;

namespace LotroKoniecDev.Frontend.Tests.Unit.Snapshots;

/// <summary>
/// Pins the whole terms page (#571); <c>TermsTests</c> checks the wording LEGAL-01 and spec 0011 depend on.
/// A new verified file here means the terms themselves changed.
/// </summary>
public sealed class TermsMarkupSnapshotTests : BunitContext
{
    public TermsMarkupSnapshotTests()
    {
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = "https://localhost:5003/",
            Authority = "https://localhost:5003",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "profile", "api"]
        }));
    }

    [Fact]
    public async Task Render_MatchesTheTermsOfServiceMarkup()
    {
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        await Verifier.Verify(component.Markup, "html");
    }
}
