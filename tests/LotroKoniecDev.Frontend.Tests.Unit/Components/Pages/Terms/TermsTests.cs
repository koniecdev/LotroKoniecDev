using System.Reflection;
using AngleSharp.Dom;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TermsComponent = LotroKoniecDev.Frontend.Components.Pages.Terms.Terms;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Terms;

/// <summary>
/// Renders the terms-of-service page (#453). This page is what the erasure design rests on: the
/// contribution licence section has to mention distribution in polish.txt and that anonymized
/// contributions are kept after an account is deleted, and the fan-project disclaimer has to name the IP
/// owners. A change in that wording would quietly break what LEGAL-01 assumes.
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
        // The registration form links here for the consent checkbox, so a visitor who is not logged in
        // must never be sent to the login page while reading what they are asked to accept.
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
    public void Render_GeneralSection_NamesTheOperator()
    {
        // UŚUDE art. 5 identification duty + the §5 contributor license grantee (LEGAL-11 / spec
        // 0011 Q5): the Operator must be a named natural person, not the non-entity "społeczność".
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IElement generalSection = component.Find("#postanowienia-ogolne");
        generalSection.TextContent.ShouldContain("Artur Koniec");
        generalSection.TextContent.ShouldContain("koniecdev@gmail.com");
    }

    [Fact]
    public void Render_IpSection_StatesTakedownComplianceAndPolishOnlyPublishedFile()
    {
        // The promise that the published file contains only the community's Polish text and never the
        // English source matters (spec 0011 E6). Changing it needs an ADR, so a change in this
        // wording has to fail loudly.
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IElement ipSection = component.Find("#wlasnosc-intelektualna");
        ipSection.TextContent.ShouldContain("Standing Stone Games");
        ipSection.TextContent.ShouldContain("Middle-earth Enterprises");
        ipSection.TextContent.ShouldContain("wyłącznie polskie teksty");
        ipSection.TextContent.ShouldContain("nigdy angielskie teksty źródłowe");
        ipSection.TextContent.ShouldContain("niezwłocznie");
        ipSection.TextContent.ShouldContain("koniecdev@gmail.com");
    }

    [Fact]
    public void Render_TableOfContents_LinksEverySection()
    {
        IRenderedComponent<TermsComponent> component = Render<TermsComponent>();

        IReadOnlyList<IElement> tocLinks = component.FindAll(".legal-toc a");
        tocLinks.Count.ShouldBe(9);
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
