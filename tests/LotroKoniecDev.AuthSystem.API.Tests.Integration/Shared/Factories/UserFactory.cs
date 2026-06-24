using Bogus;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;

internal static class UserFactory
{
    private const string DefaultPassword = "TestPass1!";

    public static RegisterRequest GenerateRandomRegisterRequest(Faker faker, string? password = null) => new(
        faker.Internet.UserName() + faker.Random.AlphaNumeric(4),
        faker.Internet.Email(),
        password ?? DefaultPassword,
        AcceptedPrivacyPolicy: true,
        AcceptedDataProcessingConsent: true);

    public static async Task<IdentityId> RegisterRandomUserAsync(
        TestApiClient apiClient,
        Faker faker,
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy,
        string? password = null)
    {
        (_, IdentityId identityId) = await RegisterRandomUserWithRequestAsync(
            apiClient, faker, accountConfirmationEmailSpy, password);
        return identityId;
    }

    public static async Task<(RegisterRequest Request, IdentityId Response)> RegisterRandomUserWithRequestAsync(
        TestApiClient apiClient,
        Faker faker,
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy,
        string? password = null)
    {
        accountConfirmationEmailSpy.Reset();

        RegisterRequest request = GenerateRandomRegisterRequest(faker, password);

        HttpResponseMessage httpResponseMessage = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative),
            request);

        string stringResponse = await httpResponseMessage.EnsureSuccessWithDetailsAsync();
        httpResponseMessage.StatusCode.ShouldBe(HttpStatusCode.Created);

        IdentityId identityId = JsonSerializer.Deserialize<IdentityId>(stringResponse, apiClient.JsonOptions);
        identityId.Value.ShouldNotBe(Guid.Empty);

        // Confirm email using token captured by spy
        await ConfirmEmailAsync(apiClient, accountConfirmationEmailSpy);

        return (request, identityId);
    }

    public static async Task<(RegisterRequest Request, IdentityId Response)> RegisterRandomUserUnconfirmedAsync(
        TestApiClient apiClient,
        Faker faker,
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy,
        string? password = null)
    {
        accountConfirmationEmailSpy.Reset();

        RegisterRequest request = GenerateRandomRegisterRequest(faker, password);

        HttpResponseMessage httpResponseMessage = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative),
            request);

        string stringResponse = await httpResponseMessage.EnsureSuccessWithDetailsAsync();
        httpResponseMessage.StatusCode.ShouldBe(HttpStatusCode.Created);

        IdentityId identityId = JsonSerializer.Deserialize<IdentityId>(stringResponse, apiClient.JsonOptions);
        identityId.Value.ShouldNotBe(Guid.Empty);

        return (request, identityId);
    }

    public static async Task ConfirmEmailAsync(
        TestApiClient apiClient,
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy)
    {
        accountConfirmationEmailSpy.LastEmail.ShouldNotBeNullOrEmpty("Expected email confirmation spy to have captured an email.");
        accountConfirmationEmailSpy.LastConfirmationToken.ShouldNotBeNullOrEmpty("Expected email confirmation spy to have captured a token.");

        ConfirmEmailRequest confirmRequest = new(accountConfirmationEmailSpy.LastEmail, accountConfirmationEmailSpy.LastConfirmationToken);

        HttpResponseMessage confirmResponse = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        await confirmResponse.EnsureSuccessWithDetailsAsync();
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
