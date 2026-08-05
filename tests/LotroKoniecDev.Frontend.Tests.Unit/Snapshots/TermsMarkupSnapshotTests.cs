using LotroKoniecDev.Frontend.Settings;
using Microsoft.Extensions.DependencyInjection;
using TermsComponent = LotroKoniecDev.Frontend.Components.Pages.Terms.Terms;

namespace LotroKoniecDev.Frontend.Tests.Unit.Snapshots;

/// <summary>
/// Pins the rendered markup of the terms-of-service page (#571). <c>TermsTests</c> asserts the load-
/// bearing wording LEGAL-01/spec 0011 depends on; the snapshot pins the rest of the document, so a
/// clause silently dropped, reordered or reworded — in a page nobody re-reads — shows up as a diff.
/// </summary>
/// <remarks>
/// A legal text is the ideal snapshot target: it is long, entirely static, and its churn rate is
/// meant to be near zero. Re-accepting this verified file is a deliberate act — it means the terms
/// themselves changed.
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
