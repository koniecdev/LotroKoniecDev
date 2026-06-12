using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class RegisterEndpointTests : EndpointsTestBase
{
    public RegisterEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task Register_ShouldReturnCreated_WhenValidDataIsProvided()
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        string content = await response.EnsureSuccessWithDetailsAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        IdentityId registerResponse =
            JsonSerializer.Deserialize<IdentityId>(content, ApiClient.JsonOptions);
        registerResponse.Value.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Register_ShouldReturnUnprocessableEntity_WhenEmailAlreadyExists()
    {
        // Arrange
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        RegisterRequest duplicateRequest = new(
            Faker.Internet.UserName() + Faker.Random.AlphaNumeric(4),
            existingRequest.Email,
            "TestPass1!",
            Faker.Person.PolishPhoneNumber(),
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Register_ShouldReturnUnprocessableEntity_WhenUsernameAlreadyExists()
    {
        // Arrange
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        RegisterRequest duplicateRequest = new(
            existingRequest.Username,
            Faker.Internet.Email(),
            "TestPass1!",
            Faker.Person.PolishPhoneNumber(),
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Register_ShouldReturnBadRequest_WhenPrivacyPolicyNotAccepted()
    {
        // Arrange
        RegisterRequest request = new(
            Faker.Internet.UserName() + Faker.Random.AlphaNumeric(4),
            Faker.Internet.Email(),
            "TestPass1!",
            Faker.Person.PolishPhoneNumber(),
            AcceptedPrivacyPolicy: false,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_ShouldReturnBadRequest_WhenPasswordIsTooWeak()
    {
        // Arrange
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker, password: "weak");

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_ShouldAutoConfirmEmail_WhenEmailSendingFails()
    {
        // Arrange
        AccountConfirmationEmailSpy.Reset();
        AccountConfirmationEmailSpy.ShouldFail = true;
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert - registration should still succeed
        await response.EnsureSuccessWithDetailsAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Assert - user should be able to login without email confirmation
        // (because auto-confirm fallback should have activated)
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = request.Username,
            ["password"] = request.Password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api offline_access"
        });

        HttpResponseMessage tokenResponse = await ApiClient.Http.PostAsync(
            new Uri("connect/token", UriKind.Relative), tokenRequest);

        await tokenResponse.EnsureSuccessWithDetailsAsync();
        tokenResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
