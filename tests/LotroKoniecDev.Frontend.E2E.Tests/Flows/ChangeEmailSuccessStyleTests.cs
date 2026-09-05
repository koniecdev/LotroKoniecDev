using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Regression guard for #719: after a successful e-mail change request the page rendered the
/// confirmation in the error style, so a user who did everything right was shown a red box. The bug is
/// fixed, and this test fails again the moment the success style is swapped back.
/// Nothing has to be seeded: the flow creates the account itself.
/// </summary>
public sealed class ChangeEmailSuccessStyleTests : E2ETestBase
{
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    public ChangeEmailSuccessStyleTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Email_change_request_is_confirmed_in_the_success_style()
    {
        // Arrange: a fresh confirmed account, signed in through the FE.
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);

        // The cookie banner is a fixed bar at the bottom and would cover the submit button (LEGAL-04).
        await AuthActions.AcceptCookieBannerAsync(Page);

        // Act: ask for a new address. A second random user is only a source of an address that is
        // guaranteed to be free, because every generated e-mail ends in its own GUID.
        string newEmail = TestUser.CreateRandom().Email;
        await Page.GetByTestId("nav-account").ClickAsync();
        await Page.GetByTestId("account-change-email").ClickAsync();
        await Page.Locator("#new-email").WaitForAsync(LongWait);
        await Page.Locator("#new-email").FillAsync(newEmail);
        await Page.Locator("#repeat-email").FillAsync(newEmail);
        await Page.Locator("#current-password").FillAsync(user.Password);
        await Page.GetByTestId("change-email-submit").ClickAsync();

        // Assert: this is what #719 was about. The confirmation has to carry the success style, and no
        // error alert may sit next to it. Waiting for the success box alone would still pass if the page
        // rendered both, which is exactly the shape the bug had.
        await Page.Locator(".status-message.status-success").WaitForAsync(LongWait);
        (await Page.Locator(".status-message.status-success").InnerTextAsync()).ShouldContain(newEmail);
        (await Page.Locator(".error-message").CountAsync()).ShouldBe(0);
    }
}
