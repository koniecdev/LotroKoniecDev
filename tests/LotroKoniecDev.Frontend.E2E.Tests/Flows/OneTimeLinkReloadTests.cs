using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// #886: a reload of the success page after a confirmed address or a new password must be a GET, send the
/// form only once and still show the success answer. Each flow creates its own account.
/// </summary>
public sealed class OneTimeLinkReloadTests : E2ETestBase
{
    /// <summary>
    /// A change request sends two e-mails. Only this one, sent to the new address, carries the confirm link.
    /// </summary>
    private const string ConfirmNewAddressSubject = "Potwierdź nowy adres e-mail";

    private const string PasswordResetSubject = "Reset hasła";
    private const string ConfirmEmailChangePath = "/Account/ConfirmEmailChange";
    private const string ResetPasswordPath = "/Account/ResetPassword";

    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };
    private static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(45);

    public OneTimeLinkReloadTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Reload_after_confirming_the_new_address_shows_the_change_as_done_again()
    {
        // Arrange
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await AuthActions.LoginAsync(Page, Fixture, user);
        await AuthActions.AcceptCookieBannerAsync(Page);
        string newEmail = TestUser.CreateRandomEmail();
        await AuthActions.RequestEmailChangeAsync(Page, user, newEmail);
        string confirmLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, newEmail, ConfirmNewAddressSubject, ConfirmEmailChangePath, MailTimeout);
        await Page.GotoAsync(confirmLink);
        PostWatch posts = PostWatch.StartCounting(Page);
        ILocator success = Page.GetByTestId("confirm-email-change-success");
        await Page.GetByTestId("confirm-email-change-submit").ClickAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Act
        IResponse? reload = await Page.ReloadAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Assert
        reload.ShouldNotBeNull();
        reload.Request.Method.ShouldBe("GET");
        posts.PostsTo(ConfirmEmailChangePath).ShouldBe(1);
        (await Page.GetByRole(AriaRole.Alert).CountAsync()).ShouldBe(0);
        (await success.CountAsync()).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reload_after_setting_a_new_password_shows_the_password_as_changed_again()
    {
        // Arrange
        TestUser user = TestUser.CreateRandom();
        await AuthActions.RegisterAsync(Page, Fixture, user);
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        await Page.GotoAsync($"{Fixture.AuthBaseUrl}/Account/ForgotPassword");
        await Page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Wyślij link", Exact = true }).ClickAsync();
        string resetLink = await MailpitClient.WaitForLinkAsync(
            Fixture.MailpitBaseUrl, user.Email, PasswordResetSubject, ResetPasswordPath, MailTimeout);
        await Page.GotoAsync(resetLink);
        string newPassword = TestUser.ComposePassword("Reset");
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Nowe hasło", Exact = true }).FillAsync(newPassword);
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "Powtórz nowe hasło", Exact = true }).FillAsync(newPassword);
        PostWatch posts = PostWatch.StartCounting(Page);
        ILocator success = Page.GetByTestId("reset-password-success");
        await Page.GetByTestId("reset-password-submit").ClickAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Act
        IResponse? reload = await Page.ReloadAsync();
        await success.Or(Page.GetByRole(AriaRole.Alert)).First.WaitForAsync(LongWait);

        // Assert
        reload.ShouldNotBeNull();
        reload.Request.Method.ShouldBe("GET");
        posts.PostsTo(ResetPasswordPath).ShouldBe(1);
        (await Page.GetByRole(AriaRole.Alert).CountAsync()).ShouldBe(0);
        (await success.CountAsync()).ShouldBe(1);
        CspViolations.ShouldBeEmpty();
    }
}
