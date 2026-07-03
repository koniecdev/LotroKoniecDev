using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
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
            ["Username"] = request.Username,
            ["Password"] = request.Password
        });

        // Assert — the message guides the user to activate, without ever stating the account exists but is unconfirmed
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("Nieprawidłowa nazwa użytkownika lub hasło");
        html.ShouldContain("aktywacyjny");
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
            ["Username"] = "nobody-" + Faker.Random.AlphaNumeric(8),
            ["Password"] = "WhateverPass1!"
        });
        string wrongPasswordMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Username"] = confirmed.Username,
            ["Password"] = confirmed.Password + "WRONG"
        });
        string unconfirmedMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Username"] = unconfirmed.Username,
            ["Password"] = unconfirmed.Password
        });
        string lockedOutMessage = await PostAndExtractRenderedAlertAsync(new Dictionary<string, string>
        {
            ["Username"] = lockedOut.Username,
            ["Password"] = lockedOut.Password // correct password — the lockout branch must still win
        });

        // Assert — no branch reveals which check failed: identical text is the anti-enumeration invariant
        nonExistentMessage.ShouldNotBeNullOrWhiteSpace();
        wrongPasswordMessage.ShouldBe(nonExistentMessage);
        unconfirmedMessage.ShouldBe(nonExistentMessage);
        lockedOutMessage.ShouldBe(nonExistentMessage);
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

    private async Task<HttpResponseMessage> PostToLoginPageAsync(Dictionary<string, string> formFields)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(
            new Uri("/Account/Login", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Login");
        request.Content = content;

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await ApiClient.Http.SendAsync(request);
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
