using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The website's "Zarejestruj" button starts its sign-in with <c>prompt=create</c>, so the sign-in server opens
/// the sign-up form. After the e-mail check, the form's "Przejdź do logowania" carries that sign-in on, and the
/// visitor ends on the website, signed in (#1575).
/// </summary>
public sealed class SignUpFromTheTopBarTests : E2ETestBase
{
    private static readonly LocatorWaitForOptions LongWait = new() { Timeout = 30_000 };

    [Fact]
    public async Task Sign_up_from_the_top_bar_then_confirm_then_log_in_ends_on_the_website_signed_in()
    {
        // Arrange
        TestUser user = TestUser.CreateRandom();
        await Page.GotoAsync($"{Fixture.FrontendBaseUrl}/");

        // Act
        await Page.GetByRole(AriaRole.Link, new() { Name = Links.SignUp, Exact = true }).ClickAsync();
        await Page.WaitForURLAsync(
            url => new Uri(url).AbsolutePath == "/Account/Register",
            new() { Timeout = 30_000 });
        await AuthActions.SubmitRegisterFormAsync(Page, user);

        // The mail link opens in a second tab, as from a mail client, so this tab keeps its way to the login.
        IPage mailTab = await Context.NewPageAsync();
        await AuthActions.ConfirmEmailAsync(mailTab, Fixture, user);
        await mailTab.CloseAsync();

        await Page.GetByRole(AriaRole.Link, new() { Name = Links.GoToLogin, Exact = true }).ClickAsync();
        await Page.GetByLabel(FieldLabels.Email).FillAsync(user.Email);
        await Page.GetByLabel(FieldLabels.Password, new() { Exact = true }).FillAsync(user.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Login, Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).WaitForAsync(LongWait);

        // Assert
        Page.Url.ShouldStartWith(Fixture.FrontendBaseUrl);
        (await Page.GetByText(user.Username).IsVisibleAsync()).ShouldBeTrue();
        CspViolations.ShouldBeEmpty();
    }

    public SignUpFromTheTopBarTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }
}
