using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.DependencyInjection;
using TermsComponent = LotroKoniecDev.Frontend.Components.Pages.Terms.Terms;

namespace LotroKoniecDev.Frontend.Tests.Unit.Snapshots;

/// <summary>
/// Pins the rendered markup of the terms-of-service page (#571). <c>TermsTests</c> checks the wording
/// LEGAL-01 and spec 0011 depend on, and the snapshot pins the rest of the document. So a clause that is
/// dropped, moved or reworded, on a page nobody reads again, shows up as a diff.
/// </summary>
/// <remarks>
/// A legal text is a perfect snapshot target: it is long, completely static, and it should almost never
/// change. Accepting a new verified file here is a deliberate act: it means the terms themselves
/// changed.
/// </remarks>
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
