using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// LEGAL-04 — the cookie information banner's acceptance criteria, pinned in a real browser with
/// <em>JavaScript disabled</em>: a new visitor sees the banner on any page, accepting it (a plain
/// SSR form post — no script) hides it everywhere, and the consent survives navigation. Needs no
/// account and no seeded database — the banner is anonymous by design.
/// </summary>
public sealed class CookieConsentBannerTests : E2ETestBase
{
    public CookieConsentBannerTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Banner_shows_for_new_visitor_and_accepting_without_javascript_hides_it_across_navigation()
    {
        // Arrange — a dedicated JS-less context: the acceptance path must be pure SSR.
        await using IBrowserContext jsLessContext = await Fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            JavaScriptEnabled = false,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 }
        });
        jsLessContext.SetDefaultTimeout(20_000);
        IPage page = await jsLessContext.NewPageAsync();

        // A brand-new visitor sees the banner on the home page…
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        ILocator banner = page.GetByRole(AriaRole.Region, new() { Name = "Informacja o plikach cookie" });
        await banner.WaitForAsync();

        // …and on any other page.
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/regulamin");
        (await banner.IsVisibleAsync()).ShouldBeTrue();

        // Accepting is a plain form post that works without JavaScript.
        await page.GetByRole(AriaRole.Button, new() { Name = "Akceptuję", Exact = true }).ClickAsync();
        await banner.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

        // The accept redirect returns to the page the form was posted from.
        page.Url.ShouldContain("/regulamin");

        // The consent cookie survives navigation — the banner stays gone everywhere.
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        (await banner.IsVisibleAsync()).ShouldBeFalse();
    }
}
