using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// The auth origin's strict CSP (#693): styles and scripts only from files on the origin, with no inline
/// style or script (#833). A page that breaks it still loads, only unstyled or without its script, so only
/// a real browser can tell (#670). The two link pages are also opened with a link that parses, so the form
/// state is checked too.
/// </summary>
public sealed class AuthPagesContentSecurityPolicyTests : E2ETestBase
{
    private const string DummyLinkQuery = "?email=a%40b.pl&token=z";

    public AuthPagesContentSecurityPolicyTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Every_account_page_renders_under_its_own_csp_without_a_violation()
    {
        // Arrange
        string[] pages =
        [
            "/Account/Login",
            "/Account/Register",
            "/Account/ForgotPassword",
            "/Account/ResendConfirmation",
            "/Account/ResetPassword",
            "/Account/PrivacyPolicy",
            "/Account/ConfirmEmail",
            "/Account/ConfirmEmailChange",
            "/Account/RevertEmailChange",
            "/Account/RevertEmailChange?userId=00000000-0000-0000-0000-000000000001&from=a%40b.pl&to=c%40d.pl&token=z",
            "/Account/CancelDeletion",
            "/Account/CancelDeletion" + DummyLinkQuery
        ];
        List<string> unstyledPages = [];

        // Act
        foreach (string page in pages)
        {
            IResponse? response = await Page.GotoAsync(
                Fixture.AuthBaseUrl + page, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
            response.ShouldNotBeNull();
            response.Headers.ShouldContainKey("content-security-policy");

            // A stylesheet the browser refused, or one that did not load, has no sheet at all, so this also
            // catches a violation the listener missed.
            bool sharedStylesheetApplied = await Page.EvaluateAsync<bool>(
                """
                () => {
                    const links = [...document.querySelectorAll('link[rel=stylesheet]')];
                    return links.some(link => link.getAttribute('href') === '/account.css')
                        && links.every(link => link.sheet !== null)
                        && document.querySelectorAll('style').length === 0;
                }
                """);
            if (!sharedStylesheetApplied)
            {
                unstyledPages.Add(page);
            }
        }

        // Assert
        unstyledPages.ShouldBeEmpty();
        CspViolations.ShouldBeEmpty();
    }

    /// <summary>
    /// Every other CSP check relies on this listener; if it stopped firing, they would all stay green.
    /// </summary>
    [Fact]
    public async Task Violation_listener_reports_a_style_the_csp_blocks()
    {
        // Arrange
        await Page.GotoAsync(Fixture.AuthBaseUrl + "/Account/Login");

        // Act: an inline style block, which style-src 'self' refuses
        bool injectedStyleApplied = await Page.EvaluateAsync<bool>(
            """
            () => {
                const style = document.createElement('style');
                style.textContent = 'body { outline: 9px solid red; }';
                document.head.appendChild(style);
                return style.sheet !== null;
            }
            """);

        // Assert: the report comes back through a binding, so give it a moment to arrive
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (CspViolations.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Page.WaitForTimeoutAsync(100);
        }

        injectedStyleApplied.ShouldBeFalse();
        CspViolations.ShouldContain(violation => violation.Contains("style-src", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_page_script_still_toggles_the_password_under_the_csp()
    {
        // Arrange: the toggle's script comes from a file, because this CSP blocks inline script
        await Page.GotoAsync(Fixture.AuthBaseUrl + "/Account/Login");
        ILocator password = Page.GetByLabel(FieldLabels.Password, new() { Exact = true });

        // Act
        await Page.GetByRole(AriaRole.Button, new() { Name = "Pokaż hasło", Exact = true }).ClickAsync();

        // Assert
        (await password.GetAttributeAsync("type")).ShouldBe("text");
        CspViolations.ShouldBeEmpty();
    }
}
