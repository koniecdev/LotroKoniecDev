using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #719: the success at the end of an e-mail change must not paint in the error style. The bug was a
/// missing CSS rule, so the check reads the colour the browser paints, not the markup. The flow creates
/// its own account and walks to the auth server's page behind the link.
/// </summary>
public sealed class ChangeEmailSuccessStyleTests : E2ETestBase
{
    /// <summary>
    /// The old address gets a warning with no link; only this one carries the link.
    /// </summary>
    private const string ConfirmNewAddressSubject = "Potwierdź nowy adres e-mail";

    private const string ConfirmEmailChangeLinkPath = "/Account/ConfirmEmailChange";

    /// <summary>
    /// Through a canvas: the stylesheet writes oklch, and browsers spell a computed colour back in more than
    /// one way.
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
        // Arrange
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);
        await AuthActions.AcceptCookieBannerAsync(Page);

        // Act
        string newEmail = TestUser.CreateRandomEmail();
        await AuthActions.RequestEmailChangeAsync(Page, user, newEmail);

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

        // The done view names no address since #886, because anybody can open its URL.
        string confirmation = await successBox.InnerTextAsync();
        confirmation.ShouldContain("Adres e-mail Twojego konta został zmieniony");

        int[] background = await successBox.EvaluateAsync<int[]>(ReadBackgroundChannels);
        int red = background[0];
        int green = background[1];
        green.ShouldBeGreaterThan(red, "a confirmed change has to be painted in the success colour, not in the error red");

        int errorBoxes = await Page.GetByTestId("confirm-email-change-error").CountAsync();
        errorBoxes.ShouldBe(0);

        // The success colour comes from the page's inline style, which the auth CSP admits by nonce (#693).
        CspViolations.ShouldBeEmpty();
    }
}
