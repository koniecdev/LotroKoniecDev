using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed partial class ResetPasswordPageTests : EndpointsTestBase
{
    public ResetPasswordPageTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ResetPasswordPage_ShouldRevokeExistingRefreshTokens_AfterReset()
    {
        // Arrange — a confirmed user with an active refresh token (offline_access)
        const string originalPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, originalPassword);

        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Username,
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

        // Obtain a reset token
        PasswordResetEmailSpy.Reset();
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));
        string resetToken = PasswordResetEmailSpy.LastResetToken!;

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

        // Act — try to use the refresh token that was issued BEFORE the reset
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshRequest);

        // Assert — the pre-reset refresh token must be dead
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> PostToResetPasswordPageAsync(Dictionary<string, string> formFields)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(
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

        return await ApiClient.Http.SendAsync(request);
    }

    private static string? ExtractAntiForgeryToken(string html)
    {
        Match match = AntiForgeryTokenRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
