using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The onboarding loop across both origins: register, confirm through Mailpit, sign in through the
/// frontend's OIDC challenge, sign out. Nothing has to be seeded.
/// </summary>
public sealed class RegisterConfirmLoginLogoutTests : E2ETestBase
{
    public RegisterConfirmLoginLogoutTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Register_then_confirm_email_then_login_then_logout()
    {
        // Arrange
        TestUser user = TestUser.CreateRandom();

        // Act and assert: registration shows the "check your email" panel.
        await AuthActions.RegisterAsync(Page, Fixture, user);
        (await ByTestId(TestIds.RegisterSuccess).IsVisibleAsync()).ShouldBeTrue();

        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        (await ByTestId(TestIds.ConfirmEmailSuccess).IsVisibleAsync()).ShouldBeTrue();

        await AuthActions.LoginAsync(Page, Fixture, user);
        (await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).IsVisibleAsync())
            .ShouldBeTrue();

        // The username from the `name` claim, not the e-mail (ADR-0022).
        (await Page.GetByText(user.Username).IsVisibleAsync()).ShouldBeTrue();

        await AuthActions.LogoutAsync(Page);
        (await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).IsVisibleAsync())
            .ShouldBeTrue();

        // Including the redirects after the login POST, which Chrome checks against form-action (#693).
        CspViolations.ShouldBeEmpty();
    }
}
