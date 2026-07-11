using System.Reflection;
using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TermsComponent = LotroKoniecDev.Frontend.Components.Pages.Terms.Terms;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Terms;

/// <summary>
/// Renders the terms-of-service page (#453). The page is the contractual anchor for the erasure
/// design: the contribution-license section must name distribution in polish.txt and post-deletion
/// retention of anonymized contributions, and the fan-project disclaimer must name the IP owners —
/// a wording regression there would silently void what LEGAL-01 assumes.
/// </summary>
public sealed class TermsTests : BunitContext
{
    public TermsTests()
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
    public void Terms_IsAnonymousByContract()
    {
        // Registration links here for the consent checkbox — an anonymous visitor must never be
        // bounced to login while reading what they are asked to accept.
        typeof(TermsComponent).GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
    }

    [Fact]
    public void Render_ContributionLicenseSection_CoversDistributionAndPostDeletionRetention()
    {
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IElement licenseSection = component.Find("#licencja");
        licenseSection.TextContent.ShouldContain("polish.txt");
        licenseSection.TextContent.ShouldContain("nieodwołalnej");
        licenseSection.TextContent.ShouldContain("Licencja pozostaje w mocy po usunięciu konta");
        licenseSection.TextContent.ShouldContain("zanonimizowanej");
        licenseSection.TextContent.ShouldContain("sublicencji");
    }

    [Fact]
    public void Render_GeneralSection_CarriesTheNonAffiliationDisclaimer()
    {
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IElement generalSection = component.Find("#postanowienia-ogolne");
        generalSection.TextContent.ShouldContain("Standing Stone Games");
        generalSection.TextContent.ShouldContain("Middle-earth Enterprises");
        generalSection.TextContent.ShouldContain("nie jest powiązany");
    }

    [Fact]
    public void Render_PrivacyPolicyLink_PointsAtTheAuthServerFromSettings()
    {
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IElement link = component.Find("#dane-osobowe a[target=_blank]");
        link.GetAttribute("href").ShouldBe("https://localhost:5003/Account/PrivacyPolicy");
    }

    [Fact]
    public void Render_TableOfContents_LinksEverySection()
    {
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IReadOnlyList<IElement> tocLinks = component.FindAll(".legal-toc a");
        tocLinks.Count.ShouldBe(8);
        foreach (IElement tocLink in tocLinks)
        {
            string anchor = tocLink.GetAttribute("href")!.TrimStart('#');
            component.Find($"#{anchor}").ShouldNotBeNull();
        }
    }

    [Fact]
    public void Render_FinalSection_StatesPolishLawAndTheChangeProcedure()
    {
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IElement finalSection = component.Find("#postanowienia-koncowe");
        finalSection.TextContent.ShouldContain("prawo polskie");
        finalSection.TextContent.ShouldContain("14 dni");
    }
}
