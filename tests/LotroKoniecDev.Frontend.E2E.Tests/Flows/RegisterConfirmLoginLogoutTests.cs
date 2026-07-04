using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// Golden path: a brand-new visitor registers on the Auth server, confirms their e-mail through the
/// link Mailpit captured, logs in via the Frontend's OIDC challenge, and logs out — proving the whole
/// account-onboarding loop across both origins (Frontend + Auth). Needs no seeded database and no
/// <c>exported.txt</c>: the user, the e-mail and the session are all created by the flow itself.
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

        // Act + Assert — registration shows the "check your email" confirmation panel.
        await AuthActions.RegisterAsync(Page, Fixture, user);
        (await ByTestId(TestIds.RegisterSuccess).IsVisibleAsync()).ShouldBeTrue();

        // Confirm via the Mailpit link — the Auth page reports success.
        await AuthActions.ConfirmEmailAsync(Page, Fixture, user);
        (await ByTestId(TestIds.ConfirmEmailSuccess).IsVisibleAsync()).ShouldBeTrue();

        // Log in through the FE OIDC challenge — the authenticated nav (the logout button) appears.
        await AuthActions.LoginAsync(Page, Fixture, user);
        (await Page.GetByRole(AriaRole.Button, new() { Name = Buttons.Logout, Exact = true }).IsVisibleAsync())
            .ShouldBeTrue();

        // The nav greets with the USERNAME (the `name` claim), not the e-mail the user logged in
        // with — the display-only-handle half of ADR-0022, proven across the whole OIDC loop.
        (await Page.GetByText(user.Username).IsVisibleAsync()).ShouldBeTrue();

        // Log out — the anonymous nav (the login link) returns.
        await AuthActions.LogoutAsync(Page);
        (await Page.GetByRole(AriaRole.Link, new() { Name = Links.Login, Exact = true }).IsVisibleAsync())
            .ShouldBeTrue();
    }
}
