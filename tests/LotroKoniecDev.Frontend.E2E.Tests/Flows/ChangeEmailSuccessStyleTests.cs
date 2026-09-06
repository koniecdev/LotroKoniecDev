using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Regression guard for #719: the page that ends an e-mail change told the user the change had worked,
/// but printed it in the error style, so a red box announced a success. That page belongs to the auth
/// server and is step 2 of 2, behind the link sent to the new mailbox, so the flow here has to walk all
/// the way to it.
/// The bug was a missing CSS rule and not a missing class name, so the check reads the colour the
/// browser really paints. A test that only looked for the word "success" in the markup would have stayed
/// green through the whole bug.
/// Nothing has to be seeded: the flow creates the account itself.
/// </summary>
public sealed class ChangeEmailSuccessStyleTests : E2ETestBase
{
    /// <summary>
    /// A change request sends two e-mails. The old address gets a warning with no link, and only this
    /// one, sent to the new address, carries the link that finishes the change.
    /// </summary>
    private const string ConfirmNewAddressSubject = "Potwierdź nowy adres e-mail";

    private const string ConfirmEmailChangeLinkPath = "/Account/ConfirmEmailChange";

    /// <summary>
    /// Gives back the red, green and blue channels of an element's background. The stylesheet writes its
    /// colours in oklch, and browsers may spell a computed colour back in more than one way, so the value
    /// goes through a canvas, which turns any CSS colour into plain numbers.
    /// </summary>
    private const string ReadBackgroundChannels =
        """
        el => {
            const context = document.createElement('canvas').getContext('2d');
            context.fillStyle = getComputedStyle(el).backgroundColor;
            context.fillRect(0, 0, 1, 1);
            const pixel = context.getImageData(0, 0, 1, 1).data;
            return [pixel[0], pixel[1], pixel[2]];
        }
        """;

    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };
    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(45);

    public ChangeEmailSuccessStyleTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Confirming_the_new_address_shows_the_result_in_the_success_style()
    {
        // Arrange: a fresh confirmed account, signed in through the FE.
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);
        await AuthActions.AcceptCookieBannerAsync(Page);

        // Act, step 1 of 2: ask for the new address on the frontend.
        string newEmail = TestUser.CreateRandomEmail();
        await Page.GetByTestId("nav-account").ClickAsync();
        await Page.GetByTestId("account-change-email").ClickAsync();
        await Page.Locator("#new-email").WaitForAsync(LongWait);
        await Page.Locator("#new-email").FillAsync(newEmail);
        await Page.Locator("#repeat-email").FillAsync(newEmail);
        await Page.Locator("#current-password").FillAsync(user.Password);
        await Page.GetByTestId("change-email-submit").ClickAsync();

        // Act, step 2 of 2: open the link from the new mailbox and confirm there. This is the page #719
        // was reported on.
        string confirmLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl,
            newEmail,
            ConfirmNewAddressSubject,
            ConfirmEmailChangeLinkPath,
            MailTimeout);
        await Page.GotoAsync(confirmLink);
        await Page.GetByTestId("confirm-email-change-submit").ClickAsync();

        // Assert
        ILocator successBox = Page.GetByTestId("confirm-email-change-success");
        await successBox.WaitForAsync(LongWait);

        string confirmation = await successBox.InnerTextAsync();
        confirmation.ShouldContain(newEmail);

        int[] background = await successBox.EvaluateAsync<int[]>(ReadBackgroundChannels);
        int red = background[0];
        int green = background[1];
        green.ShouldBeGreaterThan(red, "a confirmed change has to be painted in the success colour, not in the error red");

        int errorBoxes = await Page.GetByTestId("confirm-email-change-error").CountAsync();
        errorBoxes.ShouldBe(0);
    }
}
