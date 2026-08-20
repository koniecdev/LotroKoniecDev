using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// LEGAL-04 — the cookie information banner's acceptance criteria, pinned in a real browser: with
/// <em>JavaScript disabled</em>, a new visitor sees the banner on any page, accepting it (a plain
/// SSR form post — no script) hides it everywhere, and the consent survives navigation; and on a
/// phone-sized viewport the bar stays pinned to the bottom edge yet leaves the footer's legal links
/// reachable (#672). Needs no account and no seeded database — the banner is anonymous by design.
/// </summary>
public sealed class CookieConsentBannerTests : E2ETestBase
{
    private const string BannerLabel = "Informacja o plikach cookie";
    private const string AcceptButtonName = "Akceptuję";
    private const string TermsLinkName = "Regulamin";
    private const string PrivacyPolicyLinkName = "Polityka prywatności";
    private const int PhoneViewportWidth = 390;
    private const int PhoneViewportHeight = 844;

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
        ILocator banner = page.GetByRole(AriaRole.Region, new() { Name = BannerLabel });
        await banner.WaitForAsync();

        // …and on any other page.
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/regulamin");
        (await banner.IsVisibleAsync()).ShouldBeTrue();

        // Accepting is a plain form post that works without JavaScript.
        await page.GetByRole(AriaRole.Button, new() { Name = AcceptButtonName, Exact = true }).ClickAsync();
        await banner.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

        // The accept redirect returns to the page the form was posted from.
        page.Url.ShouldContain("/regulamin");

        // The consent cookie survives navigation — the banner stays gone everywhere.
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        (await banner.IsVisibleAsync()).ShouldBeFalse();
    }

    /// <summary>
    /// #672, the half of the trade that is easy to lose: the bar is sticky rather than in normal flow,
    /// so it is still glued to the bottom edge of the viewport at the top of a long page. A plain
    /// in-flow bar would satisfy the reachability test below just as well while scrolling the consent
    /// notice off-screen entirely — which is LEGAL-04's whole point — so it is asserted separately.
    /// </summary>
    [Fact]
    public async Task Banner_on_a_phone_viewport_stays_pinned_to_the_bottom_edge_while_the_page_can_still_scroll()
    {
        await using IBrowserContext phoneContext = await NewPhoneContextAsync();
        IPage page = await OpenTermsPageAsync(phoneContext);
        ILocator banner = page.GetByRole(AriaRole.Region, new() { Name = BannerLabel });
        await banner.WaitForAsync();

        // The terms page is far taller than the viewport, so an in-flow bar would be below the fold.
        LocatorBoundingBoxResult bar = await RequireBoundingBoxAsync(banner);

        (bar.Y + bar.Height).ShouldBe(PhoneViewportHeight, 1d);
    }

    /// <summary>
    /// #672 — the bar used to be <c>position: fixed</c>, i.e. out of flow, so at the end of the page it
    /// sat on top of the last ~165px of the document: exactly where "Regulamin" and "Polityka
    /// prywatności" live, the two links a visitor who has not consented yet most needs. The invariant
    /// is stated at <em>maximum scroll</em> on purpose — a bar pinned to the bottom edge passes over
    /// the footer mid-scroll by design, and it is where the visitor comes to rest that must be clear.
    /// JavaScript is on here purely as the test's measuring tape (scroll to the end, read boxes); the
    /// banner's own no-JavaScript contract is pinned by the JS-less test above.
    /// </summary>
    [Fact]
    public async Task Banner_on_a_phone_viewport_leaves_the_legal_footer_links_reachable_at_the_end_of_the_page()
    {
        await using IBrowserContext phoneContext = await NewPhoneContextAsync();
        IPage page = await OpenTermsPageAsync(phoneContext);
        ILocator banner = page.GetByRole(AriaRole.Region, new() { Name = BannerLabel });
        await banner.WaitForAsync();
        ILocator footer = page.GetByRole(AriaRole.Contentinfo);
        ILocator termsLink = footer.GetByRole(AriaRole.Link, new() { Name = TermsLinkName, Exact = true });
        ILocator privacyPolicyLink = footer.GetByRole(AriaRole.Link, new() { Name = PrivacyPolicyLinkName, Exact = true });

        // Act — a first-time visitor hunting for the legal texts scrolls to the very end of the page.
        await ScrollToBottomAsync(page);

        // Assert — the whole footer comes to rest above the bar, which is strictly stronger than the
        // two links doing so, and both links really take a tap: the trial click runs Playwright's
        // hit-target check, so an element painted over the link fails it.
        LocatorBoundingBoxResult bar = await RequireBoundingBoxAsync(banner);
        LocatorBoundingBoxResult footerBox = await RequireBoundingBoxAsync(footer);
        LocatorBoundingBoxResult termsBox = await RequireBoundingBoxAsync(termsLink);
        LocatorBoundingBoxResult privacyPolicyBox = await RequireBoundingBoxAsync(privacyPolicyLink);

        (footerBox.Y + footerBox.Height).ShouldBeLessThanOrEqualTo(bar.Y + 1);
        (termsBox.Y + termsBox.Height).ShouldBeLessThanOrEqualTo(bar.Y);
        (privacyPolicyBox.Y + privacyPolicyBox.Height).ShouldBeLessThanOrEqualTo(bar.Y);
        await termsLink.ClickAsync(new LocatorClickOptions { Trial = true });
        await privacyPolicyLink.ClickAsync(new LocatorClickOptions { Trial = true });
    }

    /// <summary>
    /// #672 — the other end of the same trade: nothing may stay reserved for a bar that is gone. The
    /// bar reserves its space by being in flow, so consent removing it from the markup is what makes
    /// the footer flush again; this would fail a re-implementation that padded the footer instead.
    /// </summary>
    [Fact]
    public async Task Accepting_on_a_phone_viewport_leaves_the_footer_flush_with_the_end_of_the_page()
    {
        await using IBrowserContext phoneContext = await NewPhoneContextAsync();
        IPage page = await OpenTermsPageAsync(phoneContext);
        ILocator banner = page.GetByRole(AriaRole.Region, new() { Name = BannerLabel });
        await banner.WaitForAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = AcceptButtonName, Exact = true }).ClickAsync();
        await banner.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
        await page.WaitForLoadStateAsync();
        await WaitForFontsAsync(page);
        await ScrollToBottomAsync(page);

        LocatorBoundingBoxResult footerBox = await RequireBoundingBoxAsync(page.GetByRole(AriaRole.Contentinfo));

        (footerBox.Y + footerBox.Height).ShouldBe(PhoneViewportHeight, 1d);
    }

    /// <summary>
    /// The reporter's viewport size: below 640px the bar stacks and its button goes full width, so it
    /// is at its tallest exactly where the viewport is shortest. This emulates the size, not the
    /// device — real iOS Safari is closed out by the ticket's manual retest on staging.
    /// </summary>
    private async Task<IBrowserContext> NewPhoneContextAsync()
    {
        IBrowserContext phoneContext = await Fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = PhoneViewportWidth, Height = PhoneViewportHeight }
        });
        phoneContext.SetDefaultTimeout(20_000);
        return phoneContext;
    }

    private async Task<IPage> OpenTermsPageAsync(IBrowserContext context)
    {
        IPage page = await context.NewPageAsync();
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/regulamin");
        await WaitForFontsAsync(page);
        return page;
    }

    /// <summary>Every face in <c>fonts.css</c> is <c>font-display: swap</c>, so a late swap would
    /// reflow the document after it was measured — settle them before reading any box.</summary>
    private static async Task WaitForFontsAsync(IPage page) =>
        await page.EvaluateAsync<bool>("document.fonts.ready.then(() => true)");

    private static async Task ScrollToBottomAsync(IPage page) =>
        await page.EvaluateAsync("window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' })");

    private static async Task<LocatorBoundingBoxResult> RequireBoundingBoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync()
        ?? throw new InvalidOperationException($"'{locator}' has no bounding box — it is not rendered.");
}
