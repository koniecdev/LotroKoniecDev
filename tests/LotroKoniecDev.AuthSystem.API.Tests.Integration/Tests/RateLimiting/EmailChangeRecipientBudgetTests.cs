using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The e-mail change budget that follows the inbox of the new address, whichever account asks (#793,
/// ADR-0055). The IP policy cannot see it: an attacker who rotates IPs gets a fresh budget every time, and
/// the inbox they typed in gets every link. The new address has no account yet, so the key is the inbox
/// of the address after Identity's normalizer (#835, ADR-0057).
/// They run on the suite's normal Testing host, where the limiter middleware is off, so the only thing
/// that can refuse a request here is a budget taken in the handler.
/// </summary>
public sealed class EmailChangeRecipientBudgetTests : EndpointsTestBase
{
    /// <summary>The shipped send budget, read from the same constant the registration uses.</summary>
    private const int RecipientPermitLimit = AccountBudgets.EmailChangeRecipientPermitLimit;

    private const string Password = "TestPass1!";
    private const string WrongPassword = "Wrong-Password1!";
    private const string ThrottledErrorCode = "Auth.EmailChangeRecipientThrottled";

    private static readonly Uri ChangeEmailEndpoint = new("auth/account/change-email", UriKind.Relative);

    public EmailChangeRecipientBudgetTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task RequestEmailChange_ShouldRefuseWithTooManyRequests_OnceTheRecipientBudgetIsSpent()
    {
        // Arrange
        string accessToken = await RegisterAndGetAccessTokenAsync();
        string newEmail = NewAddress();

        for (int i = 0; i < RecipientPermitLimit; i++)
        {
            using HttpResponseMessage spend = await RequestChangeAsync(accessToken, newEmail, Password);
            spend.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        using HttpResponseMessage refused = await RequestChangeAsync(accessToken, newEmail, Password);

        // Assert: a 429 with its own code, never a silent 200 (ADR-0055 §5)
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(ThrottledErrorCode);
        (await CountEmailChangeRowsAsync()).ShouldBe(RecipientPermitLimit);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldShareTheRecipientBudgetAcrossAccounts()
    {
        // Arrange: accounts are cheap to make, so a budget per requesting account would multiply by the
        // number of accounts. The inbox is what needs the limit.
        string firstAccountToken = await RegisterAndGetAccessTokenAsync();
        string secondAccountToken = await RegisterAndGetAccessTokenAsync();
        string newEmail = NewAddress();

        for (int i = 0; i < RecipientPermitLimit; i++)
        {
            using HttpResponseMessage spend = await RequestChangeAsync(firstAccountToken, newEmail, Password);
            spend.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        using HttpResponseMessage refused = await RequestChangeAsync(secondAccountToken, newEmail, Password);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(ThrottledErrorCode);
        (await CountEmailChangeRowsAsync()).ShouldBe(RecipientPermitLimit);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldCountEverySpellingOfTheNewAddressAgainstOneBudget()
    {
        // Arrange: Identity treats these as one address, so the budget has to as well
        string accessToken = await RegisterAndGetAccessTokenAsync();
        string newEmail = NewAddress();
        string[] spellings =
        [
            newEmail.ToLowerInvariant(),
            newEmail.ToUpperInvariant(),
            char.ToUpperInvariant(newEmail[0]) + newEmail[1..].ToLowerInvariant()
        ];
        spellings.Length.ShouldBe(RecipientPermitLimit);

        foreach (string spelling in spellings)
        {
            using HttpResponseMessage spend = await RequestChangeAsync(accessToken, spelling, Password);
            spend.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        using HttpResponseMessage refused = await RequestChangeAsync(accessToken, newEmail, Password);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(ThrottledErrorCode);
        (await CountEmailChangeRowsAsync()).ShouldBe(RecipientPermitLimit);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldNotSpendTheBudgetOfAnotherAddress()
    {
        // Arrange: flooding one inbox must not block a change to any other address
        string accessToken = await RegisterAndGetAccessTokenAsync();
        string floodedEmail = NewAddress();

        for (int i = 0; i < RecipientPermitLimit + 1; i++)
        {
            using HttpResponseMessage flood = await RequestChangeAsync(accessToken, floodedEmail, Password);
        }

        // Act
        using HttpResponseMessage other = await RequestChangeAsync(
            accessToken, NewAddress(), Password);

        // Assert
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CountEmailChangeRowsAsync()).ShouldBe(RecipientPermitLimit + 1);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldNotSpendTheRecipientBudget_WhenTheChangeIsRefusedEarlier()
    {
        // Arrange: the budget counts links that really go out, so a request refused for a wrong password
        // spends none. The refusals after the password check are covered where the reserved address is
        // (EmailChangeRevertReservationTests).
        string accessToken = await RegisterAndGetAccessTokenAsync();
        string newEmail = NewAddress();

        for (int i = 0; i < RecipientPermitLimit + 1; i++)
        {
            using HttpResponseMessage wrong = await RequestChangeAsync(accessToken, newEmail, WrongPassword);
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Act
        HttpStatusCode[] statusCodes = new HttpStatusCode[RecipientPermitLimit];
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await RequestChangeAsync(accessToken, newEmail, Password);
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.OK);
        (await CountEmailChangeRowsAsync()).ShouldBe(RecipientPermitLimit);
    }

    /// <summary>
    /// The budget is one singleton for the whole test collection and is never reset, so every test needs
    /// an address no other test can produce.
    /// </summary>
    private static string NewAddress() => $"Recipient-{Guid.CreateVersion7()}@Example.com";

    private async Task<string> RegisterAndGetAccessTokenAsync()
    {
        (RegisterRequest request, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        return await GetAccessTokenAsync(request.Email, Password);
    }

    private async Task<HttpResponseMessage> RequestChangeAsync(string accessToken, string newEmail, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, ChangeEmailEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangeEmailRequest(newEmail, password));

        return await ApiClient.Http.SendAsync(request);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.TryGetProperty("errorCode", out JsonElement errorCode)
            ? errorCode.GetString()
            : null;
    }

    private async Task<int> CountEmailChangeRowsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .CountAsync(row => row.Type == nameof(EmailChangeRequested));
    }
}
