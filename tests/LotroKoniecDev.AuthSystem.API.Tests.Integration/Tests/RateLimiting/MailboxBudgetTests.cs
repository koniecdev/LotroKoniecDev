using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// Every flow that mails a typed address, driven with <c>+tag</c> and dot spellings of one Gmail inbox
/// (#835, ADR-0057). Gmail delivers all of them to one inbox, so they must share one budget per flow.
/// They run on the suite's normal Testing host, where the limiter middleware is off, so the only thing
/// that can refuse a request here is a budget taken in the handler.
/// </summary>
public sealed partial class MailboxBudgetTests : EndpointsTestBase
{
    private const string Password = "TestPass1!";
    private const string RegistrationThrottledErrorCode = "Auth.RegistrationMailboxThrottled";
    private const string EmailChangeThrottledErrorCode = "Auth.EmailChangeRecipientThrottled";

    private static readonly Uri RegisterEndpoint = new("auth/register", UriKind.Relative);
    private static readonly Uri RegisterPage = new("/Account/Register", UriKind.Relative);
    private static readonly Uri ForgotPasswordEndpoint = new("auth/forgot-password", UriKind.Relative);
    private static readonly Uri ResendConfirmationEndpoint = new("auth/resend-email-confirmation", UriKind.Relative);
    private static readonly Uri ChangeEmailEndpoint = new("auth/account/change-email", UriKind.Relative);

    public MailboxBudgetTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task Register_ShouldRefuseWithTooManyRequests_OnceSpellingsOfOneInboxSpendTheBudget()
    {
        // Arrange
        GmailInbox inbox = GmailInbox.New();
        string[] spellings = [inbox.Tagged("1"), inbox.Dotted, inbox.OnGoogleMail];
        spellings.Length.ShouldBe(AccountBudgets.RegistrationPermitLimit);

        foreach (string spelling in spellings)
        {
            using HttpResponseMessage created = await RegisterAsync(spelling);
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // Act
        using HttpResponseMessage refused = await RegisterAsync(inbox.Tagged("2"));

        // Assert: refused before the mail is queued, and the new account is rolled back with it
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(RegistrationThrottledErrorCode);
        (await CountOutboxRowsAsync(nameof(EmailConfirmationRequested))).ShouldBe(spellings.Length);
        (await CountAccountsAsync(inbox.Tagged("2"))).ShouldBe(0);
    }

    [Fact]
    public async Task Register_ShouldNotSpendTheBudget_WhenTheRegistrationIsRefusedEarlier()
    {
        // Arrange: an address that is already taken sends no mail, so it must cost nothing
        GmailInbox inbox = GmailInbox.New();
        using HttpResponseMessage first = await RegisterAsync(inbox.Plain);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        for (int i = 0; i < AccountBudgets.RegistrationPermitLimit; i++)
        {
            using HttpResponseMessage duplicate = await RegisterAsync(inbox.Plain);
            duplicate.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        // Act
        HttpStatusCode[] statusCodes = new HttpStatusCode[AccountBudgets.RegistrationPermitLimit - 1];
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await RegisterAsync(inbox.Tagged($"later{i}"));
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.Created);
        (await CountOutboxRowsAsync(nameof(EmailConfirmationRequested))).ShouldBe(AccountBudgets.RegistrationPermitLimit);
    }

    [Fact]
    public async Task RegisterPage_ShouldExplainTheRefusalInPolish_WhenTheInboxBudgetIsSpent()
    {
        // Arrange: the page and the endpoint share the handler, so they share the budget too
        GmailInbox inbox = GmailInbox.New();
        for (int i = 0; i < AccountBudgets.RegistrationPermitLimit; i++)
        {
            using HttpResponseMessage created = await RegisterAsync(inbox.Tagged($"api{i}"));
            created.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        // Act
        using HttpResponseMessage refused = await PostToRegisterPageAsync(inbox.Dotted);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = WebUtility.HtmlDecode(await refused.Content.ReadAsStringAsync());
        html.ShouldContain("Na tę skrzynkę pocztową założono ostatnio zbyt wiele kont");
        (await CountAccountsAsync(inbox.Dotted)).ShouldBe(0);
    }

    [Fact]
    public async Task ResendConfirmation_ShouldShareOneBudget_BetweenUnconfirmedAccountsAtSpellingsOfOneInbox()
    {
        // Arrange: two accounts a stranger registered at spellings of one inbox must not bring two budgets
        GmailInbox inbox = GmailInbox.New();
        string[] accounts = [inbox.Tagged("a"), inbox.Dotted];
        foreach (string account in accounts)
        {
            await RegisterUnconfirmedAsync(account);
        }

        AccountConfirmationEmailSpy.Reset();

        // Act: one more than the budget, alternating between the two accounts
        for (int i = 0; i < AccountBudgets.EmailConfirmationResendPermitLimit + 1; i++)
        {
            using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
                ResendConfirmationEndpoint, new ResendEmailConfirmationRequest(accounts[i % accounts.Length]));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Assert
        AccountConfirmationEmailSpy.CallCount.ShouldBe(AccountBudgets.EmailConfirmationResendPermitLimit);
    }

    [Fact]
    public async Task ForgotPassword_ShouldShareOneBudget_BetweenUnconfirmedAccountsAtSpellingsOfOneInbox()
    {
        // Arrange
        GmailInbox inbox = GmailInbox.New();
        string[] accounts = [inbox.Tagged("a"), inbox.OnGoogleMail];
        foreach (string account in accounts)
        {
            await RegisterUnconfirmedAsync(account);
        }

        // Act: one more than the budget, alternating between the two accounts
        for (int i = 0; i < AccountBudgets.PasswordResetPermitLimit + 1; i++)
        {
            using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
                ForgotPasswordEndpoint, new ForgotPasswordRequest(accounts[i % accounts.Length]));
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Assert
        (await CountOutboxRowsAsync(nameof(PasswordResetRequested))).ShouldBe(AccountBudgets.PasswordResetPermitLimit);
    }

    [Fact]
    public async Task ForgotPassword_ShouldKeepTheOwnersBudget_WhenAStrangersAccountAtHerInboxSpendsItsOwn()
    {
        // Arrange: Anna is confirmed. A stranger registers a +tag spelling of her inbox, which stays
        // unconfirmed because its link goes to Anna, and floods resets for it. None of those mails can
        // reset Anna's own password, so they must not use up her budget (ADR-0057).
        GmailInbox inbox = GmailInbox.New();
        await RegisterUnconfirmedAsync(inbox.Plain);
        await UserFactory.ConfirmEmailAsync(ApiClient, AccountConfirmationEmailSpy);
        await RegisterUnconfirmedAsync(inbox.Tagged("stranger"));

        for (int i = 0; i < AccountBudgets.PasswordResetPermitLimit + 1; i++)
        {
            using HttpResponseMessage flood = await ApiClient.Http.PostAsJsonAsync(
                ForgotPasswordEndpoint, new ForgotPasswordRequest(inbox.Tagged("stranger")));
            flood.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        int rowsAfterFlood = await CountOutboxRowsAsync(nameof(PasswordResetRequested));

        // Act
        for (int i = 0; i < AccountBudgets.PasswordResetPermitLimit; i++)
        {
            using HttpResponseMessage own = await ApiClient.Http.PostAsJsonAsync(
                ForgotPasswordEndpoint, new ForgotPasswordRequest(inbox.Plain));
            own.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Assert
        rowsAfterFlood.ShouldBe(AccountBudgets.PasswordResetPermitLimit);
        (await CountOutboxRowsAsync(nameof(PasswordResetRequested)))
            .ShouldBe(rowsAfterFlood + AccountBudgets.PasswordResetPermitLimit);
    }

    [Fact]
    public async Task RequestEmailChange_ShouldCountEverySpellingOfTheNewInboxAgainstOneBudget()
    {
        // Arrange
        (RegisterRequest requester, _) = await UserFactory.RegisterRandomUserWithRequestAsync(
            ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(requester.Email, Password);

        GmailInbox inbox = GmailInbox.New();
        string[] spellings = [inbox.Tagged("1"), inbox.Dotted, inbox.OnGoogleMail];
        spellings.Length.ShouldBe(AccountBudgets.EmailChangeRecipientPermitLimit);

        foreach (string spelling in spellings)
        {
            using HttpResponseMessage spend = await RequestEmailChangeAsync(accessToken, spelling);
            spend.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        using HttpResponseMessage refused = await RequestEmailChangeAsync(accessToken, inbox.Plain);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(EmailChangeThrottledErrorCode);
        (await CountOutboxRowsAsync(nameof(EmailChangeRequested))).ShouldBe(spellings.Length);
    }

    private async Task<HttpResponseMessage> RegisterAsync(string email)
    {
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker, Password) with { Email = email };
        return await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, request);
    }

    private async Task RegisterUnconfirmedAsync(string email)
    {
        AccountConfirmationEmailSpy.Reset();

        using HttpResponseMessage created = await RegisterAsync(email);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The confirmation mail travels the outbox, so wait for it before the next step reads the spy.
        await AccountConfirmationEmailSpy.WaitForCaptureAsync();
    }

    private async Task<HttpResponseMessage> RequestEmailChangeAsync(string accessToken, string newEmail)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, ChangeEmailEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new ChangeEmailRequest(newEmail, Password));

        return await ApiClient.Http.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostToRegisterPageAsync(string email)
    {
        using HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(RegisterPage);
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);

        Dictionary<string, string> formFields = new()
        {
            ["Username"] = Faker.Random.AlphaNumeric(16),
            ["Email"] = email,
            ["Password"] = Password,
            ["ConfirmPassword"] = Password,
            ["AcceptedPrivacyPolicy"] = "true",
            ["AcceptedDataProcessingConsent"] = "true",
            ["AcceptedTermsOfService"] = "true"
        };
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, RegisterPage);
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

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        string content = await response.Content.ReadAsStringAsync();
        using JsonDocument json = JsonDocument.Parse(content);
        return json.RootElement.TryGetProperty("errorCode", out JsonElement errorCode)
            ? errorCode.GetString()
            : null;
    }

    private async Task<int> CountOutboxRowsAsync(string type)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.OutboxMessages
            .AsNoTracking()
            .CountAsync(row => row.Type == type);
    }

    private async Task<int> CountAccountsAsync(string email)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        string normalizedEmail = email.ToUpperInvariant();
        return await db.Users
            .AsNoTracking()
            .CountAsync(user => user.NormalizedEmail == normalizedEmail);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    /// <summary>
    /// Spellings of one Gmail inbox. The budgets are singletons for the whole test collection and are never
    /// reset, so every test builds its own inbox that no other test can produce.
    /// </summary>
    private sealed record GmailInbox(string Name)
    {
        public string Plain => $"{Name}@gmail.com";

        public string Dotted => $"{Name[..4]}.{Name[4..]}@gmail.com";

        public string OnGoogleMail => $"{Name}@googlemail.com";

        public string Tagged(string tag) => $"{Name}+{tag}@gmail.com";

        public static GmailInbox New() => new($"inbox{Guid.CreateVersion7():N}");
    }
}
