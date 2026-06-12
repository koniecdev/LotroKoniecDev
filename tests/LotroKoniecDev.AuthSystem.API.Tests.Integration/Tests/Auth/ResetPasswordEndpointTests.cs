using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class ResetPasswordEndpointTests : EndpointsTestBase
{
    public ResetPasswordEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ResetPassword_ShouldReturnOk_WhenTokenIsValid()
    {
        // Arrange
        const string originalPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, originalPassword);

        PasswordResetEmailSpy.Reset();

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));

        string resetToken = PasswordResetEmailSpy.LastResetToken!;

        ResetPasswordRequest resetRequest = new(registerRequest.Email, resetToken, newPassword);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative), resetRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResetPassword_ShouldAllowLoginWithNewPassword_AfterReset()
    {
        // Arrange
        const string originalPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, originalPassword);

        PasswordResetEmailSpy.Reset();

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(registerRequest.Email, PasswordResetEmailSpy.LastResetToken!, newPassword));

        // Act — login with new password
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Username,
            ["password"] = newPassword,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        json.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task ResetPassword_ShouldInvalidateOldPassword_AfterReset()
    {
        // Arrange
        const string originalPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, originalPassword);

        PasswordResetEmailSpy.Reset();

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(registerRequest.Email, PasswordResetEmailSpy.LastResetToken!, newPassword));

        // Act — try login with old password
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Username,
            ["password"] = originalPassword,
            ["client_id"] = "lotrokoniecdev-test"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_ShouldReturnBadRequest_WhenTokenIsInvalid()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        ResetPasswordRequest resetRequest = new(
            registerRequest.Email,
            "invalid-token",
            "NewPass99!");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative), resetRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_ShouldReturnBadRequest_WhenEmailDoesNotExist()
    {
        // Arrange
        ResetPasswordRequest resetRequest = new(
            "nonexistent@example.com",
            "some-token",
            "NewPass99!");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative), resetRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_ShouldReturnBadRequest_WhenNewPasswordIsTooWeak()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        PasswordResetEmailSpy.Reset();

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));

        ResetPasswordRequest resetRequest = new(
            registerRequest.Email,
            PasswordResetEmailSpy.LastResetToken!,
            "weak");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative), resetRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ResetPassword_ShouldReturnBadRequest_WhenTokenIsUsedTwice()
    {
        // Arrange
        const string originalPassword = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, originalPassword);

        PasswordResetEmailSpy.Reset();

        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/forgot-password", UriKind.Relative),
            new ForgotPasswordRequest(registerRequest.Email));

        string resetToken = PasswordResetEmailSpy.LastResetToken!;

        // First reset — should succeed
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(registerRequest.Email, resetToken, "NewPass99!"));

        // Act — second reset with same token should fail
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative),
            new ResetPasswordRequest(registerRequest.Email, resetToken, "AnotherPass1!"));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
