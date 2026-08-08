using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed partial class LoginPageTests : EndpointsTestBase
{
    /// <summary>
    /// Where a sign-in without a usable continuation has to land: the frontend's own login route,
    /// derived from the web client origin this host is configured with.
    /// </summary>
    private const string ExpectedFrontendLoginUrl = AuthSystemApiFactory.TestFrontendAppRoot + "/auth/login";

    public LoginPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    /// <summary>
    /// ADR-0046: this branch runs after the password check, so it names the actual problem instead of
    /// asserting the credentials are wrong — and carries the address into the resend page, which is
    /// the only action that unblocks the account.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldNameTheUnconfirmedAccount_WhenThePasswordIsCorrect()
    {
        // Arrange — a registered account whose e-mail was never confirmed, with a valid password
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act — the correct credentials, but the account is still unconfirmed
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Email,
            ["Password"] = request.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("To konto nie zostało jeszcze aktywowane");
        html.ShouldNotContain("Nieprawidłowy e-mail lub hasło");
        html.ShouldContain($"/Account/ResendConfirmation?email={Uri.EscapeDataString(request.Email)}");
    }

    /// <summary>
    /// The affordance half of the invariant below: a caller without the password is not offered the
    /// resend link either, so the link cannot become the oracle the message no longer is. The page
    /// always carries a bare <c>/Account/ResendConfirmation</c> in its footer — only the address-bearing
    /// form of it is account-specific.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldNotOfferTheResendLink_WhenTheUnconfirmedAccountsPasswordIsWrong()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Email,
            ["Password"] = request.Password + "WRONG"
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldNotContain("/Account/ResendConfirmation?email=");
    }

    [Fact]
    public async Task LoginPage_ShouldRejectLogin_WhenIdentifierIsUsernameInsteadOfEmail()
    {
        // Arrange — a confirmed account; the login identifier is the e-mail (ADR-0022)
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act — the valid username with the valid password must behave like any wrong credential
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Username,
            ["Password"] = request.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Nieprawidłowy e-mail lub hasło");
    }

    /// <summary>
    /// The anti-enumeration invariant, as ADR-0046 restated it: every branch reachable **without** a
    /// verified password answers identically — including an unconfirmed account probed with the wrong
    /// password — so nothing about an address is learnable without already holding its password. The
    /// two branches behind a verified password (deletion scheduled, unconfirmed e-mail) name their
    /// reason on purpose and are pinned separately.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldReturnIdenticalMessage_ForEveryFailureReachableWithoutThePassword()
    {
        // Arrange — a confirmed account (wrong-password + lockout branches) and an unconfirmed one
        (RegisterRequest confirmed, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest unconfirmed, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest lockedOut, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        await LockOutAsync(lockedOut.Username);

        // Act — one probe per credential-failure branch a caller can reach without the password
        string nonExistentMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = "nobody-" + Faker.Random.AlphaNumeric(8) + "@example.com",
            ["Password"] = "WhateverPass1!"
        });
        string wrongPasswordMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = confirmed.Email,
            ["Password"] = confirmed.Password + "WRONG"
        });
        string unconfirmedWrongPasswordMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = unconfirmed.Email,
            ["Password"] = unconfirmed.Password + "WRONG"
        });
        string lockedOutMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = lockedOut.Email,
            ["Password"] = lockedOut.Password // correct password — the lockout branch must still win
        });

        // Assert — no branch reveals which check failed: identical text is the anti-enumeration invariant
        nonExistentMessage.ShouldNotBeNullOrWhiteSpace();
        wrongPasswordMessage.ShouldBe(nonExistentMessage);
        unconfirmedWrongPasswordMessage.ShouldBe(nonExistentMessage);
        lockedOutMessage.ShouldBe(nonExistentMessage);
    }

    /// <summary>
    /// Every off-site target collapses to the configured frontend instead of being carried into the
    /// <c>Location</c> header. The <c>%09</c> case earns its own row: a prefix-only guard calls it
    /// local, and handing it to <c>LocalRedirect</c> fails the executor's own check — so without the
    /// control-character screen a successful login ends in an unhandled 500 rather than a redirect.
    /// </summary>
    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example/harvest")]
    public async Task LoginPage_ShouldRedirectToTheFrontend_WhenReturnUrlIsNotLocal(string returnUrl)
    {
        // Arrange — a confirmed account, so the login itself succeeds and reaches the redirect
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(
            new Dictionary<string, string>
            {
                ["Email"] = request.Email,
                ["Password"] = request.Password
            },
            returnUrl);

        // Assert — the off-site target is dropped; the fallback comes from configuration, not the query
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(ExpectedFrontendLoginUrl);
    }

    /// <summary>
    /// The reset-password and confirm-email pages send the user to a bare <c>/Account/Login</c>, so a
    /// successful sign-in has no continuation to resume. This host's root serves the discovery JSON,
    /// which dead-ends the browser — the fallback has to leave for the frontend's login route, where
    /// the cookie just issued completes the OIDC challenge silently.
    /// </summary>
    [Fact]
    public async Task LoginPage_ShouldRedirectToTheFrontend_WhenThereIsNoReturnUrl()
    {
        // Arrange — a confirmed account, mirroring a user who just reset their password
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(new Dictionary<string, string>
        {
            ["Email"] = request.Email,
            ["Password"] = request.Password
        });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(ExpectedFrontendLoginUrl);
    }

    [Fact]
    public async Task LoginPage_ShouldResumeTheContinuation_WhenReturnUrlIsLocal()
    {
        // Arrange — the interrupted authorization the login flow is expected to resume
        const string continuation = "/connect/authorize?client_id=lotrokoniecdev-test&response_type=code";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpResponseMessage response = await PostToLoginPageAsync(
            new Dictionary<string, string>
            {
                ["Email"] = request.Email,
                ["Password"] = request.Password
            },
            continuation);

        // Assert — hardening must not break the flow it protects
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe(continuation);
    }

    private async Task LockOutAsync(string username)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = await userManager.FindByNameAsync(username)
            ?? throw new InvalidOperationException($"Test user '{username}' was not found.");

        await userManager.SetLockoutEnabledAsync(user, true);
        await userManager.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private async Task<string> PostAndExtractRenderedAlertAsync(Dictionary<string, string> formFields)
    {
        HttpResponseMessage response = await PostToLoginPageAsync(formFields);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await response.Content.ReadAsStringAsync();
        Match match = AlertMessageRegex().Match(html);
        match.Success.ShouldBeTrue("Expected the login page to render an error alert.");
        return match.Groups[1].Value.Trim();
    }

    /// <summary>
    /// Posts credentials to the login page, optionally carrying an OIDC continuation in the query
    /// string. Both legs run on one non-redirecting client — it must be the same client so its cookie
    /// container carries the antiforgery cookie from the GET into the POST, and non-redirecting so
    /// the sign-in redirect target stays observable as a <c>Location</c> header instead of being
    /// followed away.
    /// </summary>
    private async Task<HttpResponseMessage> PostToLoginPageAsync(
        Dictionary<string, string> formFields,
        string? returnUrl = null)
    {
        using HttpClient browser = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage pageResponse = await browser.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        string postUrl = returnUrl is null
            ? "/Account/Login"
            : $"/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, postUrl);
        request.Content = content;

        return await browser.SendAsync(request);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    [GeneratedRegex("""<span class="s">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex AlertMessageRegex();
}
