using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// LEGAL-04 in a real browser: the banner works with JavaScript off, and on a phone-sized screen it stays at
/// the bottom edge and leaves the footer's legal links reachable (#672). No account needed.
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

        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        ILocator banner = page.GetByRole(AriaRole.Region, new() { Name = BannerLabel });
        await banner.WaitForAsync();

        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/regulamin");
        (await banner.IsVisibleAsync()).ShouldBeTrue();

        await page.GetByRole(AriaRole.Button, new() { Name = AcceptButtonName, Exact = true }).ClickAsync();
        await banner.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });

        page.Url.ShouldContain("/regulamin");

        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        (await banner.IsVisibleAsync()).ShouldBeFalse();
    }

    /// <summary>
    /// #672: a bar in the normal flow would pass the reachability test below while scrolling the consent
    /// notice out of sight, so this is checked separately.
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
    /// #672: the legal links sit at the end of the page, where a fixed bar would cover them. Checked at
    /// maximum scroll, because a sticky bar passes over the footer while scrolling by design. JavaScript is
    /// on only to scroll and measure.
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

        // Assert: the whole footer ends up above the bar, and the trial click's hit-target check fails if
        // anything is drawn over a link.
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
    /// #672: no space may stay reserved for a bar that is gone. A footer padded for the bar would fail this.
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
    /// The reporter's screen size. Below 640px the bar is at its tallest. Real iOS Safari is covered by
    /// the manual retest on staging.
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
    /// <c>font-display: swap</c> means a late font would move the page after it was measured.
    /// </summary>
    private static async Task WaitForFontsAsync(IPage page) =>
        await page.EvaluateAsync<bool>("document.fonts.ready.then(() => true)");

    private static async Task ScrollToBottomAsync(IPage page) =>
        await page.EvaluateAsync("window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' })");

    private static async Task<LocatorBoundingBoxResult> RequireBoundingBoxAsync(ILocator locator) =>
        await locator.BoundingBoxAsync()
        ?? throw new InvalidOperationException($"'{locator}' has no bounding box — it is not rendered.");
}
