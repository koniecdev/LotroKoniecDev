using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class TokenEndpointTests : EndpointsTestBase
{
    public TokenEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task PasswordGrant_ShouldReturnTokens_WhenCredentialsAreValid()
    {
        // Arrange
        const string password = "TestPass1!";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
        json.RootElement.GetProperty("refresh_token").GetString().ShouldNotBeNullOrEmpty();
        json.RootElement.GetProperty("token_type").GetString().ShouldBe("Bearer");
        json.RootElement.GetProperty("expires_in").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PasswordGrant_ShouldReturnBadRequest_WhenPasswordIsInvalid()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Email,
            ["password"] = "WrongPassword1!",
            ["client_id"] = "lotrokoniecdev-test"
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PasswordGrant_ShouldReturnBadRequest_WhenUserDoesNotExist()
    {
        // Arrange
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = "nonexistent@example.com",
            ["password"] = "TestPass1!",
            ["client_id"] = "lotrokoniecdev-test"
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PasswordGrant_ShouldReturnBadRequest_WhenIdentifierIsUsernameInsteadOfEmail()
    {
        // Arrange — the login identifier is the e-mail (ADR-0022); a valid username must not authenticate
        const string password = "TestPass1!";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Username,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test"
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldReturnNewTokens_WhenRefreshTokenIsValid()
    {
        // Arrange
        const string password = "TestPass1!";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);

        string loginContent = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument loginJson = JsonDocument.Parse(loginContent);
        string refreshToken = loginJson.RootElement.GetProperty("refresh_token").GetString()!;

        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
        json.RootElement.GetProperty("token_type").GetString().ShouldBe("Bearer");
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldReturnUpdatedRoles_WhenRolesChangedInDatabase()
    {
        // Arrange: Register user and get initial tokens
        const string password = "TestPass1!";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "openid email profile roles api offline_access"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);

        string loginContent = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument loginJson = JsonDocument.Parse(loginContent);

        string initialAccessToken = loginJson.RootElement.GetProperty("access_token").GetString()!;
        string refreshToken = loginJson.RootElement.GetProperty("refresh_token").GetString()!;

        // Verify initial token has only "Translator" role via introspection
        using JsonDocument initialIntrospection = await IntrospectTokenAsync(initialAccessToken);
        initialIntrospection.RootElement.GetProperty("active").GetBoolean().ShouldBeTrue();
        string initialRole = initialIntrospection.RootElement.GetProperty("role").GetString()!;
        initialRole.ShouldBe("Translator");

        // Act: Add "Admin" role to the user in the database
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager =
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser? user = await userManager.FindByNameAsync(request.Username);
        user.ShouldNotBeNull();

        IdentityResult addRoleResult = await userManager.AddToRoleAsync(user, "Admin");
        addRoleResult.Succeeded.ShouldBeTrue();

        // Use refresh token to get new tokens
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        HttpResponseMessage refreshResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshRequest);

        // Assert: New access token should contain both roles
        refreshResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string refreshContent = await refreshResponse.Content.ReadAsStringAsync();
        using JsonDocument refreshJson = JsonDocument.Parse(refreshContent);

        string newAccessToken = refreshJson.RootElement.GetProperty("access_token").GetString()!;
        using JsonDocument newIntrospection = await IntrospectTokenAsync(newAccessToken);
        newIntrospection.RootElement.GetProperty("active").GetBoolean().ShouldBeTrue();

        // When multiple roles exist, the "role" claim is an array
        newIntrospection.RootElement.GetProperty("role").ValueKind.ShouldBe(JsonValueKind.Array);
        List<string> roles = newIntrospection.RootElement.GetProperty("role")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToList();

        roles.ShouldContain("Translator");
        roles.ShouldContain("Admin");
    }

    [Fact]
    public async Task ClientCredentialsGrant_ShouldReturnToken_WhenClientIsValid()
    {
        // Arrange
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AuthConstants.ClientIds.Api,
            ["client_secret"] = AuthSystemApiFactory.TestApiClientSecret,
            ["scope"] = "api service"
        });

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);

        json.RootElement.GetProperty("access_token").GetString().ShouldNotBeNullOrEmpty();
        json.RootElement.GetProperty("token_type").GetString().ShouldBe("Bearer");
    }

    [Fact]
    public async Task RefreshTokenGrant_ShouldFail_WhenRefreshTokenIsRevoked()
    {
        // Arrange: Register user and get tokens
        const string password = "TestPass1!";
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        using FormUrlEncodedContent loginRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), loginRequest);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string loginContent = await loginResponse.Content.ReadAsStringAsync();
        using JsonDocument loginJson = JsonDocument.Parse(loginContent);
        string refreshToken = loginJson.RootElement.GetProperty("refresh_token").GetString()!;

        // Revoke the refresh token via the revocation endpoint
        using FormUrlEncodedContent revokeRequest = new(new Dictionary<string, string>
        {
            ["token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        HttpResponseMessage revokeResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/revoke", UriKind.Relative), revokeRequest);
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act: Try to use the revoked refresh token
        using FormUrlEncodedContent refreshRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = "lotrokoniecdev-test"
        });

        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), refreshRequest);

        // Assert: Revoked refresh token should be rejected
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<JsonDocument> IntrospectTokenAsync(string token)
    {
        using FormUrlEncodedContent introspectRequest = new(new Dictionary<string, string>
        {
            ["token"] = token,
            ["client_id"] = AuthConstants.ClientIds.Api,
            ["client_secret"] = AuthSystemApiFactory.TestApiClientSecret
        });

        HttpResponseMessage response = await ApiClient.Http.PostAsync(
            new Uri("connect/introspect", UriKind.Relative), introspectRequest);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }
}
