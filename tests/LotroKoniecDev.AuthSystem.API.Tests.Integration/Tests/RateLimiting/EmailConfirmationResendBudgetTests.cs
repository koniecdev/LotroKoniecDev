using System.Text.RegularExpressions;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The resend budget that follows the inbox the mail would reach, not the caller asking for it
/// (#793, #835, ADR-0055, ADR-0057). The resend form is anonymous and takes any address, so the IP policy alone lets an
/// attacker who rotates IPs flood one inbox. The resend skips the outbox and calls the sender directly,
/// so the sender spy counts exactly what went out.
/// They run on the suite's normal Testing host, where the limiter middleware is off, so the only thing
/// that can refuse a request here is this budget.
/// </summary>
public sealed partial class EmailConfirmationResendBudgetTests : EndpointsTestBase
{
    /// <summary>The shipped send budget, read from the same constant the registration uses.</summary>
    private const int AccountPermitLimit = AccountBudgets.EmailConfirmationResendPermitLimit;

    private const string SuccessMarker = "data-testid=\"resend-confirmation-success\"";

    private static readonly Uri ResendConfirmationPage = new("/Account/ResendConfirmation", UriKind.Relative);
    private static readonly Uri ResendConfirmationEndpoint = new("auth/resend-email-confirmation", UriKind.Relative);

    public EmailConfirmationResendBudgetTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldStopSendingAfterTheAccountBudget()
    {
        // Arrange
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        AccountConfirmationEmailSpy.Reset();

        for (int i = 0; i < AccountPermitLimit; i++)
        {
            using HttpResponseMessage spend = await PostToResendConfirmationPageAsync(request.Email);
            spend.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act: one more than the budget, from the same client
        using HttpResponseMessage refused = await PostToResendConfirmationPageAsync(request.Email);

        // Assert: the budget bounds what was sent, and the refused request shows the same panel as the rest
        AccountConfirmationEmailSpy.CallCount.ShouldBe(AccountPermitLimit);
        refused.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await refused.Content.ReadAsStringAsync()).ShouldContain(SuccessMarker);
    }

    [Fact]
    public async Task ResendConfirmation_ShouldShareOneBudgetBetweenThePageAndTheEndpoint()
    {
        // Arrange: the endpoint has no caller in the product, but it is public, so a budget of its own would
        // simply double the page's
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        AccountConfirmationEmailSpy.Reset();

        // Act: two through each door, one more than the budget in total
        List<HttpStatusCode> statusCodes = [];
        for (int i = 0; i < 2; i++)
        {
            using HttpResponseMessage page = await PostToResendConfirmationPageAsync(request.Email);
            statusCodes.Add(page.StatusCode);

            using HttpResponseMessage endpoint = await ApiClient.Http.PostAsJsonAsync(
                ResendConfirmationEndpoint, new ResendEmailConfirmationRequest(request.Email));
            statusCodes.Add(endpoint.StatusCode);
        }

        // Assert: the endpoint refuses with the same 200 every other branch gives
        AccountConfirmationEmailSpy.CallCount.ShouldBe(AccountPermitLimit);
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldCountEverySpellingOfTheAddressAgainstOneBudget()
    {
        // Arrange: Identity finds the account whatever the letter case, so a key on the typed text would hand
        // every spelling a budget of its own
        (RegisterRequest request, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        AccountConfirmationEmailSpy.Reset();

        string[] spellings =
        [
            request.Email,
            request.Email.ToUpperInvariant(),
            request.Email.ToLowerInvariant(),
            char.ToUpperInvariant(request.Email[0]) + request.Email[1..].ToLowerInvariant()
        ];
        spellings.Length.ShouldBe(AccountPermitLimit + 1);

        // Act
        foreach (string spelling in spellings)
        {
            using HttpResponseMessage response = await PostToResendConfirmationPageAsync(spelling);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Assert
        AccountConfirmationEmailSpy.CallCount.ShouldBe(AccountPermitLimit);
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldNotSpendTheBudgetOfAnotherAccount()
    {
        // Arrange: flooding one inbox must not lock every other new user out of the resend
        (RegisterRequest floodedUser, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest otherUser, _) =
            await UserFactory.RegisterRandomUserUnconfirmedAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        for (int i = 0; i < AccountPermitLimit + 1; i++)
        {
            using HttpResponseMessage flood = await PostToResendConfirmationPageAsync(floodedUser.Email);
            flood.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        AccountConfirmationEmailSpy.Reset();

        // Act
        using HttpResponseMessage other = await PostToResendConfirmationPageAsync(otherUser.Email);

        // Assert
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        AccountConfirmationEmailSpy.CallCount.ShouldBe(1);
        AccountConfirmationEmailSpy.LastEmail.ShouldBe(otherUser.Email);
    }

    [Fact]
    public async Task ResendConfirmationPage_ShouldNotSpendTheBudgetOnAnAddressNobodyRegisteredYet()
    {
        // Arrange: the budget counts sends, so requests for an address with no account must spend nothing.
        // Otherwise a stranger could empty the budget before the owner has even registered.
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker);
        AccountConfirmationEmailSpy.Reset();

        for (int i = 0; i < AccountPermitLimit + 1; i++)
        {
            using HttpResponseMessage early = await PostToResendConfirmationPageAsync(request.Email);
            early.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        AccountConfirmationEmailSpy.CallCount.ShouldBe(0);

        using HttpResponseMessage registered = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/register", UriKind.Relative), request);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        await AccountConfirmationEmailSpy.WaitForCaptureAsync();
        AccountConfirmationEmailSpy.Reset();

        // Act
        for (int i = 0; i < AccountPermitLimit; i++)
        {
            using HttpResponseMessage response = await PostToResendConfirmationPageAsync(request.Email);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Assert: the whole budget is still there for the real account
        AccountConfirmationEmailSpy.CallCount.ShouldBe(AccountPermitLimit);
    }

    private async Task<HttpResponseMessage> PostToResendConfirmationPageAsync(string email)
    {
        using HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(ResendConfirmationPage);
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);

        Dictionary<string, string> formFields = new()
        {
            ["Email"] = email
        };
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, ResendConfirmationPage);
        request.Content = content;

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        return await ApiClient.Http.SendAsync(request);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
