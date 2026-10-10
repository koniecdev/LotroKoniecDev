using System.Net.Http.Headers;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using OpenIddict.Abstractions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class ChangePasswordEndpointTests : EndpointsTestBase
{
    public ChangePasswordEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ChangePassword_ShouldReturnOk_WhenCurrentPasswordIsCorrect()
    {
        // Arrange
        const string currentPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, currentPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, currentPassword);

        ChangePasswordRequest changeRequest = new(currentPassword, newPassword);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(changeRequest);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_ShouldAllowLoginWithNewPassword_AfterChange()
    {
        // Arrange
        const string currentPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, currentPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, currentPassword);

        using HttpRequestMessage changeReq = new(HttpMethod.Post, "auth/change-password");
        changeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        changeReq.Content = JsonContent.Create(new ChangePasswordRequest(currentPassword, newPassword));

        await ApiClient.Http.SendAsync(changeReq);

        // Act: login with new password
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Email,
            ["password"] = newPassword,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_ShouldRevokeExistingRefreshTokens_AfterChange()
    {
        // Arrange: a confirmed user with an active refresh token (offline_access)
        const string currentPassword = "TestPass1!";
        const string newPassword = "NewPass99!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, currentPassword);

        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Email,
            ["password"] = currentPassword,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginContent = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument loginJson = JsonDocument.Parse(loginContent);
        string accessToken = loginJson.RootElement.GetProperty("access_token").GetString()!;
        string refreshToken = loginJson.RootElement.GetProperty("refresh_token").GetString()!;

        // Change the password (authenticated with the pre-change access token)
        using HttpRequestMessage changeReq = new(HttpMethod.Post, "auth/change-password");
        changeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        changeReq.Content = JsonContent.Create(new ChangePasswordRequest(currentPassword, newPassword));

        HttpResponseMessage changeResponse = await ApiClient.Http.SendAsync(changeReq);
        changeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act: try to use the refresh token that was issued BEFORE the change
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshRequest);

        // Assert: the pre-change refresh token must be dead
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The stamp check refuses this refresh on its own (#848), so the row shows that the revoke ran.
        (await OpenIddictTokenState.StatusOfAsync(Factory.Services, refreshToken)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
    }

    [Fact]
    public async Task ChangePassword_ShouldReturnBadRequest_WhenCurrentPasswordIsWrong()
    {
        // Arrange
        const string currentPassword = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, currentPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, currentPassword);

        ChangePasswordRequest changeRequest = new("WrongPassword1!", "NewPass99!");

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(changeRequest);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_ShouldReturnBadRequest_WhenNewPasswordIsTooWeak()
    {
        // Arrange
        const string currentPassword = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, currentPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, currentPassword);

        ChangePasswordRequest changeRequest = new(currentPassword, "weak");

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(changeRequest);

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_ShouldReturnUnauthorized_WhenNotAuthenticated()
    {
        // Arrange
        ChangePasswordRequest changeRequest = new("TestPass1!", "NewPass99!");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/change-password", UriKind.Relative), changeRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangePassword_ShouldRefuseTheNewPasswordAndKeepTheOldOne_WhenItAppearsInDataBreaches()
    {
        // Arrange
        const string currentPassword = "TestPass1!";
        const string breachedPassword = "Password1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, currentPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, currentPassword);
        Factory.PwnedPasswords.MarkBreached(breachedPassword);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/change-password");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangePasswordRequest(currentPassword, breachedPassword));

        // Act
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("errorCode").GetString().ShouldBe("Auth.PasswordFoundInBreaches");

        using HttpResponseMessage loginWithOldPassword =
            await RequestPasswordGrantAsync(registerRequest.Email, currentPassword);
        loginWithOldPassword.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
