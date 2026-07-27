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
    public LoginPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task LoginPage_ShouldGuideToConfirmEmail_WhenAccountIsUnconfirmed()
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

        // Assert — the message guides the user to activate, without ever stating the account exists but is unconfirmed
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Nieprawidłowy e-mail lub hasło");
        html.ShouldContain("aktywacyjny");
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

    [Fact]
    public async Task LoginPage_ShouldReturnIdenticalMessage_ForEveryCredentialFailure()
    {
        // Arrange — a confirmed account (wrong-password + lockout branches) and an unconfirmed one
        (RegisterRequest confirmed, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest unconfirmed, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest lockedOut, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        await LockOutAsync(lockedOut.Username);

        // Act — one probe per credential-failure branch of /Account/Login
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
        string unconfirmedMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = unconfirmed.Email,
            ["Password"] = unconfirmed.Password
        });
        string lockedOutMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Email"] = lockedOut.Email,
            ["Password"] = lockedOut.Password // correct password — the lockout branch must still win
        });

        // Assert — no branch reveals which check failed: identical text is the anti-enumeration invariant
        nonExistentMessage.ShouldNotBeNullOrWhiteSpace();
        wrongPasswordMessage.ShouldBe(nonExistentMessage);
        unconfirmedMessage.ShouldBe(nonExistentMessage);
        lockedOutMessage.ShouldBe(nonExistentMessage);
    }

    /// <summary>
    /// Every off-site target collapses to the home page instead of being carried into the
    /// <c>Location</c> header. The <c>%09</c> case earns its own row: a prefix-only guard calls it
    /// local, and handing it to <c>LocalRedirect</c> fails the executor's own check — so without the
    /// control-character screen a successful login ends in an unhandled 500 rather than a redirect.
    /// </summary>
    [Theory]
    [InlineData("/\t/evil.example")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("https://evil.example/harvest")]
    public async Task LoginPage_ShouldRedirectHome_WhenReturnUrlIsNotLocal(string returnUrl)
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

        // Assert — the off-site target is dropped, never reflected into the Location header
        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldNotBeNull();
        response.Headers.Location!.OriginalString.ShouldBe("/");
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
