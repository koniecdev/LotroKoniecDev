using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class ConfirmEmailEndpointTests : EndpointsTestBase
{
    public ConfirmEmailEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnOk_WhenTokenIsValid()
    {
        // Arrange
        await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        ConfirmEmailRequest confirmRequest = new(AccountConfirmationEmailSpy.LastEmail!, AccountConfirmationEmailSpy.LastConfirmationToken!);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldAllowLogin_AfterConfirmation()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        // Confirm the email
        ConfirmEmailRequest confirmRequest = new(AccountConfirmationEmailSpy.LastEmail!, AccountConfirmationEmailSpy.LastConfirmationToken!);
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Act — login with confirmed user
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Email,
            ["password"] = password,
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
    public async Task ConfirmEmail_ShouldPreventLogin_WhenEmailNotConfirmed()
    {
        // Arrange
        const string password = "TestPass1!";

        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy, password);

        // Act — try to login without confirming email
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = registerRequest.Email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        HttpResponseMessage loginResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        // Assert — should fail because email is not confirmed
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnBadRequest_WhenTokenIsInvalid()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        ConfirmEmailRequest confirmRequest = new(registerRequest.Email, "invalid-token");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnBadRequest_WhenEmailDoesNotExist()
    {
        // Arrange
        ConfirmEmailRequest confirmRequest = new("nonexistent@example.com", "some-token");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnBadRequest_WhenEmailAlreadyConfirmed()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Try to confirm again
        ConfirmEmailRequest confirmRequest = new(registerRequest.Email, "any-token");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnBadRequest_WhenTokenIsUsedTwice()
    {
        // Arrange
        (RegisterRequest _, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        string email = AccountConfirmationEmailSpy.LastEmail!;
        string token = AccountConfirmationEmailSpy.LastConfirmationToken!;

        // First confirmation — should succeed
        await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative),
            new ConfirmEmailRequest(email, token));

        // Act — second confirmation with same token should fail
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative),
            new ConfirmEmailRequest(email, token));

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnBadRequest_WhenEmailIsInvalid()
    {
        // Arrange
        ConfirmEmailRequest confirmRequest = new("not-an-email", "some-token");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnBadRequest_WhenEmailIsEmpty()
    {
        // Arrange
        ConfirmEmailRequest confirmRequest = new("", "some-token");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConfirmEmail_ShouldReturnBadRequest_WhenTokenIsEmpty()
    {
        // Arrange
        ConfirmEmailRequest confirmRequest = new("user@example.com", "");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_ShouldSendConfirmationEmail_WhenUserIsCreated()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();

        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        AccountConfirmationEmailSpy.CallCount.ShouldBe(1);
        AccountConfirmationEmailSpy.LastEmail.ShouldBe(request.Email);
        AccountConfirmationEmailSpy.LastConfirmationToken.ShouldNotBeNullOrEmpty();
    }
}
