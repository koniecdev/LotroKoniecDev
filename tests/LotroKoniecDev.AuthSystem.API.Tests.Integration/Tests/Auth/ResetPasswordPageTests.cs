using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed partial class ResetPasswordPageTests : EndpointsTestBase
{
    public ResetPasswordPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ResetPasswordPage_ShouldRevokeExistingRefreshTokens_AfterReset()
    {
        // Arrange: a confirmed user with an active refresh token (offline_access)
        const string originalPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, originalPassword);

        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Email,
            ["password"] = originalPassword,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginContent = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument loginJson = JsonDocument.Parse(loginContent);
        string refreshToken = loginJson.RootElement.GetProperty("refresh_token").GetString()!;

        string resetToken = await RequestResetTokenAsync(registerRequest.Email);

        // Complete the reset through the browser Razor page
        HttpResponseMessage resetPageResponse = await PostToResetPasswordPageAsync(new Dictionary<string, string>
        {
            ["Email"] = registerRequest.Email,
            ["Token"] = resetToken,
            ["NewPassword"] = newPassword,
            ["ConfirmPassword"] = newPassword
        });
        resetPageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string resetHtml = await resetPageResponse.Content.ReadAsStringAsync();
        resetHtml.ShouldContain("Hasło zmienione"); // the IsCompleted success panel

        // Act: try to use the refresh token that was issued BEFORE the reset
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshRequest);

        // Assert: the pre-reset refresh token must be dead
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The stamp check refuses this refresh on its own (#848), so the row shows that the revoke ran.
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task ResetPasswordPage_Post_ShouldRedirectToADoneViewThatCarriesNoAccountValue()
    {
        // #886: the done answer used to be the POST's own page, so a reload sent the used link again and
        // the page called it dead. Anybody can open the done view, so its URL names no address or token.
        const string newPassword = "NewPass99!";
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, "TestPass1!");
        string resetToken = await RequestResetTokenAsync(registerRequest.Email);
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await PostToResetPasswordPageAsync(browser, new Dictionary<string, string>
        {
            ["Email"] = registerRequest.Email,
            ["Token"] = resetToken,
            ["NewPassword"] = newPassword,
            ["ConfirmPassword"] = newPassword
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/Account/ResetPassword?handler=Done");
    }

    [Fact]
    public async Task ResetPasswordPage_GetTheRedirectTarget_ShouldShowThePasswordAsChanged()
    {
        // The done view holds no state, so a reload of it gets this same answer. The browser test
        // (OneTimeLinkReloadTests) is what proves that a reload repeats this GET and not the POST.
        const string newPassword = "NewPass99!";
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, "TestPass1!");
        string resetToken = await RequestResetTokenAsync(registerRequest.Email);
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        HttpResponseMessage reset = await PostToResetPasswordPageAsync(browser, new Dictionary<string, string>
        {
            ["Email"] = registerRequest.Email,
            ["Token"] = resetToken,
            ["NewPassword"] = newPassword,
            ["ConfirmPassword"] = newPassword
        });

        HttpResponseMessage doneView = await browser.GetAsync(reset.Headers.Location!);

        doneView.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await doneView.Content.ReadAsStringAsync();
        html.ShouldContain("data-testid=\"reset-password-success\"");
        html.ShouldNotContain("data-testid=\"reset-password-error\"");
        html.ShouldNotContain("data-testid=\"reset-password-submit\"");
    }

    [Theory]
    [InlineData("WrongToken", "NewPass99!", "NewPass99!")]
    [InlineData("", "NewPass99!", "NewPass99!")]
    [InlineData("WrongToken", "NewPass99!", "OtherPass99!")]
    public async Task ResetPasswordPage_Post_ShouldAnswerWithThePageNotARedirect_WhenTheResetFails(
        string token, string newPassword, string confirmPassword)
    {
        // Only a done reset leaves the page. A refusal keeps the form or the dead-link panel in the answer.
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, "TestPass1!");
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await PostToResetPasswordPageAsync(browser, new Dictionary<string, string>
        {
            ["Email"] = registerRequest.Email,
            ["Token"] = token,
            ["NewPassword"] = newPassword,
            ["ConfirmPassword"] = confirmPassword
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("data-testid=\"reset-password-success\"");
    }

    [Fact]
    public async Task ResetPasswordPage_Post_ShouldKeepTheFormNotRedirect_WhenAGoodLinkCarriesAPasswordThePolicyRefuses()
    {
        // The link is good, so this refusal comes after the token check. A redirect here would say
        // "Hasło zmienione" while the old password still works.
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, "TestPass1!");
        string resetToken = await RequestResetTokenAsync(registerRequest.Email);
        using HttpClient browser = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        HttpResponseMessage response = await PostToResetPasswordPageAsync(browser, new Dictionary<string, string>
        {
            ["Email"] = registerRequest.Email,
            ["Token"] = resetToken,
            ["NewPassword"] = "abc",
            ["ConfirmPassword"] = "abc"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync();
        html.ShouldNotContain("data-testid=\"reset-password-success\"");
        html.ShouldContain("data-testid=\"reset-password-submit\"");
    }

    private async Task<string> RequestResetTokenAsync(string email)
    {
        PasswordResetEmailSpy.Reset();
        using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(email));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        await PasswordResetEmailSpy.WaitForCaptureAsync();

        return PasswordResetEmailSpy.LastResetToken!;
    }

    private Task<HttpResponseMessage> PostToResetPasswordPageAsync(Dictionary<string, string> formFields) =>
        PostToResetPasswordPageAsync(ApiClient.Http, formFields);

    private static async Task<HttpResponseMessage> PostToResetPasswordPageAsync(
        HttpClient client, Dictionary<string, string> formFields)
    {
        HttpResponseMessage pageResponse = await client.GetAsync(
            new Uri("/Account/ResetPassword", UriKind.Relative));
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        string? antiForgeryToken = ExtractAntiForgeryToken(html);
        if (antiForgeryToken is not null)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/ResetPassword");
        request.Content = content;

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await client.SendAsync(request);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
