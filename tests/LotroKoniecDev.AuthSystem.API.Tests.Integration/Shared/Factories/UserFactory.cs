using Bogus;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;

internal static class UserFactory
{
    private const string DefaultPassword = "TestPass1!";

    public static RegisterRequest GenerateRandomRegisterRequest(Faker faker, string? password = null) => new(
        // Faker.Internet.UserName() produces dots and underscores, which ADR-0022 forbids: letters and
        // digits only.
        faker.Random.AlphaNumeric(16),
        GenerateUniqueEmail(faker),
        password ?? DefaultPassword,
        AcceptedPrivacyPolicy: true,
        AcceptedTermsOfService: true);

    /// <summary>
    /// Mail budgets key on the inbox and outlive the database reset (ADR-0057), and a late mail can reach the
    /// spies after their reset (#950), so two tests must never share an address.
    /// </summary>
    public static string GenerateUniqueEmail(Faker faker) =>
        faker.Internet.Email(uniqueSuffix: Guid.CreateVersion7().ToString("N"));

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
        RegisterRequest request = GenerateRandomRegisterRequest(faker, password);

        HttpResponseMessage httpResponseMessage = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative),
            request);

        await httpResponseMessage.EnsureSuccessWithDetailsAsync();
        httpResponseMessage.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        IdentityId identityId = await WaitForNewAccountIdAsync(accountConfirmationEmailSpy, request);

        await ConfirmEmailAsync(apiClient, accountConfirmationEmailSpy, request.Email);

        return (request, identityId);
    }

    public static async Task<(RegisterRequest Request, IdentityId Response)> RegisterRandomUserUnconfirmedAsync(
        TestApiClient apiClient,
        Faker faker,
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy,
        string? password = null)
    {
        RegisterRequest request = GenerateRandomRegisterRequest(faker, password);

        HttpResponseMessage httpResponseMessage = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative),
            request);

        await httpResponseMessage.EnsureSuccessWithDetailsAsync();
        httpResponseMessage.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        IdentityId identityId = await WaitForNewAccountIdAsync(accountConfirmationEmailSpy, request);

        return (request, identityId);
    }

    public static async Task ConfirmEmailAsync(
        TestApiClient apiClient,
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy,
        string email)
    {
        await accountConfirmationEmailSpy.WaitForCaptureAsync(email);

        string? token = accountConfirmationEmailSpy.LastConfirmationTokenSentTo(email);
        token.ShouldNotBeNullOrEmpty("Expected email confirmation spy to have captured a token.");

        ConfirmEmailRequest confirmRequest = new(email, token);

        HttpResponseMessage confirmResponse = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        await confirmResponse.EnsureSuccessWithDetailsAsync();
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Registration answers 202 with no body (#860), so the new account's id comes from the confirmation mail,
    /// which travels the outbox.
    /// </summary>
    private static async Task<IdentityId> WaitForNewAccountIdAsync(
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy,
        RegisterRequest request)
    {
        await accountConfirmationEmailSpy.WaitForCaptureAsync(request.Email);

        Guid userId = accountConfirmationEmailSpy.ConfirmationEmailsTo(request.Email).Last().UserId;
        userId.ShouldNotBe(Guid.Empty);

        return IdentityId.Create(userId);
    }
}
