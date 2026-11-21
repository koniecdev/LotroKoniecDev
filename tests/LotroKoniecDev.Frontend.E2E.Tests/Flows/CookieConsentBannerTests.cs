using System.Collections.Concurrent;
using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The cookie notice in a real browser: it closes with no request, so nothing on the page is lost, and it
/// shows on the first page only (#1339). On a phone-sized screen it stays at the bottom edge and leaves the
/// footer's legal links reachable (#672). No account needed.
/// </summary>
public sealed class CookieConsentBannerTests : E2ETestBase
{
    private const string NoticeLabel = "Informacja o plikach cookie";
    private const string CloseButtonName = "Rozumiem";
    private const string SearchLabel = "Wyszukaj";
    private const string TypedSearch = "Mithril";
    private const string TermsLinkName = "Regulamin";
    private const string TermsHeading = "Regulamin serwisu";
    private const string PrivacyPolicyLinkName = "Polityka prywatności";
    private const int PhoneViewportWidth = 390;
    private const int PhoneViewportHeight = 844;

    public CookieConsentBannerTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    /// <summary>
    /// A reload would clear what the visitor ticked or typed, so closing must send no request (#1339).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_the_notice_sends_no_request_and_keeps_what_the_visitor_typed(bool javaScriptEnabled)
    {
        // Arrange
        await using IBrowserContext context = await NewDesktopContextAsync(javaScriptEnabled);
        IPage page = await context.NewPageAsync();
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/translations");
        ILocator notice = page.GetByRole(AriaRole.Dialog, new() { Name = NoticeLabel });
        await notice.WaitForAsync();
        ILocator search = page.GetByLabel(SearchLabel, new() { Exact = true });
        await search.FillAsync(TypedSearch);
        string urlBeforeClosing = page.Url;
        ConcurrentQueue<string> requests = WatchRequestsThatCouldReplaceThePage(page);

        // Act
        await page.GetByRole(AriaRole.Button, new() { Name = CloseButtonName, Exact = true }).ClickAsync();
        await notice.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });

        // Assert
        requests.ShouldBeEmpty();
        page.Url.ShouldBe(urlBeforeClosing);
        (await search.InputValueAsync()).ShouldBe(TypedSearch);
    }

    /// <summary>
    /// The notice only informs, so the page that shows it already stores the cookie. With JavaScript on the
    /// footer link is an enhanced navigation, which patches the page instead of loading it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Notice_shows_on_the_first_page_only_even_when_the_visitor_never_closes_it(bool javaScriptEnabled)
    {
        // Arrange
        await using IBrowserContext context = await NewDesktopContextAsync(javaScriptEnabled);
        IPage page = await context.NewPageAsync();
        await page.GotoAsync($"{Fixture.FrontendBaseUrl}/");
        ILocator notice = page.GetByRole(AriaRole.Dialog, new() { Name = NoticeLabel });
        await notice.WaitForAsync();

        // Act
        await page.GetByRole(AriaRole.Contentinfo)
            .GetByRole(AriaRole.Link, new() { Name = TermsLinkName, Exact = true })
            .ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = TermsHeading, Level = 1 }).WaitForAsync();

        // Assert
        (await notice.IsVisibleAsync()).ShouldBeFalse();
    }

    /// <summary>
    /// #672: a bar in the normal flow would pass the reachability test below while scrolling the cookie notice
    /// out of sight, so this is checked separately.
    /// </summary>
    [Fact]
    public async Task Notice_on_a_phone_viewport_stays_pinned_to_the_bottom_edge_while_the_page_can_still_scroll()
    {
        await using IBrowserContext phoneContext = await NewPhoneContextAsync();
        IPage page = await OpenTermsPageAsync(phoneContext);
        ILocator notice = page.GetByRole(AriaRole.Dialog, new() { Name = NoticeLabel });
        await notice.WaitForAsync();

        // The terms page is far taller than the viewport, so an in-flow bar would be below the fold.
        LocatorBoundingBoxResult bar = await notice.RequireBoundingBoxAsync();

        (bar.Y + bar.Height).ShouldBe(PhoneViewportHeight, 1d);
    }

    /// <summary>
    /// #672: the legal links sit at the end of the page, where a fixed bar would cover them. Checked at
    /// maximum scroll, because a sticky bar passes over the footer while scrolling by design. JavaScript is
    /// on only to scroll and measure.
    /// </summary>
    [Fact]
    public async Task Notice_on_a_phone_viewport_leaves_the_legal_footer_links_reachable_at_the_end_of_the_page()
    {
        await using IBrowserContext phoneContext = await NewPhoneContextAsync();
        IPage page = await OpenTermsPageAsync(phoneContext);
        ILocator notice = page.GetByRole(AriaRole.Dialog, new() { Name = NoticeLabel });
        await notice.WaitForAsync();
        ILocator footer = page.GetByRole(AriaRole.Contentinfo);
        ILocator termsLink = footer.GetByRole(AriaRole.Link, new() { Name = TermsLinkName, Exact = true });
        ILocator privacyPolicyLink = footer.GetByRole(AriaRole.Link, new() { Name = PrivacyPolicyLinkName, Exact = true });

        // Act: a first-time visitor looking for the legal texts scrolls to the very end of the page.
        await ScrollToBottomAsync(page);

        // Assert: the whole footer ends up above the bar, and the trial click's hit-target check fails if
        // anything is drawn over a link.
        LocatorBoundingBoxResult bar = await notice.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult footerBox = await footer.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult termsBox = await termsLink.RequireBoundingBoxAsync();
        LocatorBoundingBoxResult privacyPolicyBox = await privacyPolicyLink.RequireBoundingBoxAsync();

        (footerBox.Y + footerBox.Height).ShouldBeLessThanOrEqualTo(bar.Y + 1);
        (termsBox.Y + termsBox.Height).ShouldBeLessThanOrEqualTo(bar.Y);
        (privacyPolicyBox.Y + privacyPolicyBox.Height).ShouldBeLessThanOrEqualTo(bar.Y);
        await termsLink.ClickAsync(new LocatorClickOptions { Trial = true });
        await privacyPolicyLink.ClickAsync(new LocatorClickOptions { Trial = true });
    }

    /// <summary>
    /// #672: no space may stay reserved for a bar that is gone. A footer padded for the bar would fail this,
    /// and so would a closed dialog that a display rule keeps on screen.
    /// </summary>
    [Fact]
    public async Task Closing_on_a_phone_viewport_leaves_the_footer_flush_with_the_end_of_the_page()
    {
        await using IBrowserContext phoneContext = await NewPhoneContextAsync();
        IPage page = await OpenTermsPageAsync(phoneContext);
        ILocator notice = page.GetByRole(AriaRole.Dialog, new() { Name = NoticeLabel });
        await notice.WaitForAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = CloseButtonName, Exact = true }).ClickAsync();
        await notice.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Hidden });
        await page.WaitForFontsAsync();
        await ScrollToBottomAsync(page);

        LocatorBoundingBoxResult footerBox = await page.GetByRole(AriaRole.Contentinfo).RequireBoundingBoxAsync();

        (footerBox.Y + footerBox.Height).ShouldBe(PhoneViewportHeight, 1d);
    }

    /// <summary>
    /// A form post, a reload or a script call would show up here. Images and fonts the page loads late would
    /// not replace it, so they do not count.
    /// </summary>
    private static ConcurrentQueue<string> WatchRequestsThatCouldReplaceThePage(IPage page)
    {
        ConcurrentQueue<string> requests = new();
        page.Request += (_, request) =>
        {
            if (request.IsNavigationRequest || request.Method is not "GET" || request.ResourceType is "fetch" or "xhr")
            {
                requests.Enqueue($"{request.Method} {request.Url}");
            }
        };
        return requests;
    }

    private async Task<IBrowserContext> NewDesktopContextAsync(bool javaScriptEnabled)
    {
        IBrowserContext context = await Fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            JavaScriptEnabled = javaScriptEnabled,
            ViewportSize = new ViewportSize { Width = 1366, Height = 900 }
        });
        context.SetDefaultTimeout(20_000);
        return context;
    }

    /// <summary>
    /// The reporter's screen size. Below 640px the bar is at its tallest. These tests run in Chromium only.
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
        await page.WaitForFontsAsync();
        return page;
    }

    private static async Task ScrollToBottomAsync(IPage page) =>
        await page.EvaluateAsync("window.scrollTo({ top: document.documentElement.scrollHeight, behavior: 'instant' })");
}
