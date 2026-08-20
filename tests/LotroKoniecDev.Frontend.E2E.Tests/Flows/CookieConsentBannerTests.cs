using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// LEGAL-04: what the cookie banner has to do, checked in a real browser. With JavaScript turned off, a
/// new visitor sees the banner on any page, accepting it with a plain form post and no script hides it
/// everywhere, and the consent survives navigation. On a phone-sized screen the bar stays at the bottom
/// edge and still leaves the footer's legal links reachable (#672).
/// It needs no account and nothing seeded, because the banner is for anonymous visitors.
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
        // Arrange: a dedicated JS-less context: the acceptance path must be pure SSR.
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

        // The consent cookie survives navigation, so the banner stays hidden everywhere.
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        (await banner.IsVisibleAsync()).ShouldBeFalse();
    }

    /// <summary>
    /// #672, the half of the deal that is easy to lose: the bar is sticky and not in the normal flow, so
    /// it is still stuck to the bottom of the screen at the top of a long page.
    /// A bar in the normal flow would pass the reachability test below just as well while scrolling the
    /// consent notice out of sight, which is the whole point of LEGAL-04. So it is checked separately.
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
    /// #672: the bar used to be <c>position: fixed</c>, so it was out of the flow and, at the end of the
    /// page, sat on top of the last 165 pixels or so. That is exactly where "Regulamin" and "Polityka
    /// prywatności" are, the two links a visitor who has not consented yet needs most.
    /// The rule is stated at maximum scroll on purpose: a bar stuck to the bottom passes over the footer
    /// while scrolling by design, and what matters is where the visitor ends up.
    /// JavaScript is on here only as the test's measuring tool, to scroll to the end and read boxes. That
    /// the banner itself works without JavaScript is pinned by the test above.
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

        // Act: a first-time visitor looking for the legal texts scrolls to the very end of the page.
        await ScrollToBottomAsync(page);

        // Assert: the whole footer ends up above the bar, which is stronger than only the two links doing
        // so, and both links really respond to a tap. The trial click runs Playwright's hit-target check,
        // so anything drawn over the link fails it.
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
    /// #672, the other end of the same deal: no space may stay reserved for a bar that is gone. The bar
    /// takes up its space by being in the flow, so removing it from the markup after consent is what
    /// makes the footer sit flush again. A rewrite that padded the footer instead would fail this.
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
    /// The screen size the reporter used. Below 640px the bar stacks its parts and the button becomes
    /// full width, so it is at its tallest exactly where the screen is shortest.
    /// This copies the size and not the device; real iOS Safari is covered by the ticket's manual retest
    /// on staging.
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

    /// <summary>
    /// Every font in <c>fonts.css</c> uses <c>font-display: swap</c>, so a font that arrives late would
    /// move the page after it was measured. Wait for them before reading any box.
    /// </summary>
    private static async Task WaitForFontsAsync(IPage page) =>
        await page.EvaluateAsync<bool>("document.fonts.ready.then(() => true)");

    private static async Task ScrollToBottomAsync(IPage page) =>
        await page.EvaluateAsync("window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' })");

    private static async Task<LocatorBoundingBoxResult> RequireBoundingBoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync()
        ?? throw new InvalidOperationException($"'{locator}' has no bounding box — it is not rendered.");
}
