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
        // Unique, because the mail budgets key on the inbox and outlive the database reset between tests
        // (ADR-0057): two tests that drew the same address would share a budget.
        faker.Internet.Email(uniqueSuffix: Guid.CreateVersion7().ToString("N")),
        password ?? DefaultPassword,
        AcceptedPrivacyPolicy: true,
        AcceptedTermsOfService: true);

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

        await httpResponseMessage.EnsureSuccessWithDetailsAsync();
        httpResponseMessage.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        IdentityId identityId = await WaitForNewAccountIdAsync(accountConfirmationEmailSpy, request);

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

        await httpResponseMessage.EnsureSuccessWithDetailsAsync();
        httpResponseMessage.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        IdentityId identityId = await WaitForNewAccountIdAsync(accountConfirmationEmailSpy, request);

        return (request, identityId);
    }

    public static async Task ConfirmEmailAsync(
        TestApiClient apiClient,
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy)
    {
        await accountConfirmationEmailSpy.WaitForCaptureAsync();

        accountConfirmationEmailSpy.LastEmail.ShouldNotBeNullOrEmpty("Expected email confirmation spy to have captured an email.");
        accountConfirmationEmailSpy.LastConfirmationToken.ShouldNotBeNullOrEmpty("Expected email confirmation spy to have captured a token.");

        ConfirmEmailRequest confirmRequest = new(accountConfirmationEmailSpy.LastEmail, accountConfirmationEmailSpy.LastConfirmationToken);

        HttpResponseMessage confirmResponse = await apiClient.Http.PostAsJsonAsync(
            new Uri("auth/confirm-email", UriKind.Relative), confirmRequest);

        await confirmResponse.EnsureSuccessWithDetailsAsync();
        confirmResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Registration answers 202 with no body, whether or not the address already had an account (#860), so
    /// the new account's id comes from the confirmation mail. That mail travels the outbox pipeline
    /// (commit -> relay -> delivery), so the helper waits for it.
    /// </summary>
    private static async Task<IdentityId> WaitForNewAccountIdAsync(
        SpyAccountConfirmationEmailSender accountConfirmationEmailSpy,
        RegisterRequest request)
    {
        await accountConfirmationEmailSpy.WaitForCaptureAsync();

        accountConfirmationEmailSpy.LastEmail.ShouldBe(request.Email);
        Guid userId = accountConfirmationEmailSpy.LastUserId.ShouldNotBeNull();
        userId.ShouldNotBe(Guid.Empty);

        return IdentityId.Create(userId);
    }
}
