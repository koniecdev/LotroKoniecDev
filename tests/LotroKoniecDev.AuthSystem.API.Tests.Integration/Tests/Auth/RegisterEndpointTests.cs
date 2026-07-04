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
            Faker.Random.AlphaNumeric(16),
            existingRequest.Email,
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Register_ShouldReturnUnprocessableEntity_WhenEmailDiffersOnlyByCase()
    {
        // Arrange
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        string caseVariantEmail = existingRequest.Email.ToUpperInvariant();
        caseVariantEmail.ShouldNotBe(existingRequest.Email);

        RegisterRequest duplicateRequest = new(
            Faker.Random.AlphaNumeric(16),
            caseVariantEmail,
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Auth.UserAlreadyExistsByEmail");
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
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Register_ShouldReturnUnprocessableEntity_WhenUsernameDiffersOnlyByCase()
    {
        // Arrange — Identity normalization makes the handle uniqueness case-insensitive (ADR-0022)
        (RegisterRequest existingRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        string caseVariantUsername = existingRequest.Username.ToUpperInvariant();
        caseVariantUsername.ShouldNotBe(existingRequest.Username);

        RegisterRequest duplicateRequest = new(
            caseVariantUsername,
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), duplicateRequest);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Auth.UserAlreadyExistsByUsername");
    }

    [Theory]
    [InlineData("kasia 92")]
    [InlineData("kasia.92")]
    [InlineData("kasia_92")]
    [InlineData("kasia@92")]
    [InlineData("kaśka92")]
    [InlineData("kasia-92")]
    [InlineData("kasia92\n")] // .NET's $ matches before a trailing \n — \A…\z anchoring must reject this
    [InlineData(" kasia92")]
    public async Task Register_ShouldReturnBadRequestWithCharsetMessage_WhenUsernameHasIllegalCharacters(
        string username)
    {
        // Arrange
        RegisterRequest request = new(
            username,
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert — the explicit validator message, never Identity's raw English error surfacing late
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("Username may contain only letters and digits, without spaces.");
        content.ShouldContain("RegisterUser.Validation");
        content.ShouldNotContain("Auth.RegistrationFailed");
    }

    [Theory]
    [InlineData("kasia92")]
    [InlineData("KASIA92")]
    public async Task Register_ShouldReturnCreated_WhenUsernameIsAlphanumeric(string username)
    {
        // Arrange
        RegisterRequest request = new(
            username,
            Faker.Internet.Email(),
            "TestPass1!",
            AcceptedPrivacyPolicy: true,
            AcceptedDataProcessingConsent: true);

        // Act
        HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);

        // Assert
        await response.EnsureSuccessWithDetailsAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Register_ShouldReturnBadRequest_WhenPrivacyPolicyNotAccepted()
    {
        // Arrange
        RegisterRequest request = new(
            Faker.Random.AlphaNumeric(16),
            Faker.Internet.Email(),
            "TestPass1!",
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
            ["username"] = request.Email,
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
