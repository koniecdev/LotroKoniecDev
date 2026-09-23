using System.Data.Common;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.EmailConfirmation;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
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
    private static readonly Uri ForgotPasswordPage = new("/Account/ForgotPassword", UriKind.Relative);
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
    public async Task Register_ShouldNotSpendTheBudget_WhenTheUsernameIsTaken()
    {
        // Arrange: the other refusals (a reserved address, an Identity error) also return before the
        // permit; a taken name is the one a caller can reach at will
        GmailInbox inbox = GmailInbox.New();
        RegisterRequest first = UserFactory.GenerateRandomRegisterRequest(Faker, Password) with { Email = inbox.Plain };
        using HttpResponseMessage created = await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, first);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        for (int i = 0; i < AccountBudgets.RegistrationPermitLimit; i++)
        {
            RegisterRequest sameName = first with { Email = inbox.Tagged($"name{i}") };
            using HttpResponseMessage refused = await ApiClient.Http.PostAsJsonAsync(RegisterEndpoint, sameName);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
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
    }

    [Fact]
    public async Task Register_ShouldTakeOnePermit_WhenATransientCommitFailureReplaysTheTransaction()
    {
        // Arrange: the execution strategy replays the whole registration after a transient error on the
        // commit. A second permit for the same registration would leave an honest user one short. The
        // host is our own, so its budgets start empty and the failure touches no other test.
        GmailInbox inbox = GmailInbox.New();
        FailFirstRegistrationCommitInterceptor interceptor = new(inbox.Tagged("1"));
        await using WebApplicationFactory<Program> host = Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(interceptor))));
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage replayed = await RegisterAsync(client, inbox.Tagged("1"));
        replayed.StatusCode.ShouldBe(HttpStatusCode.Created);
        interceptor.FailedCommits.ShouldBe(1);

        // Act
        HttpStatusCode[] statusCodes = new HttpStatusCode[AccountBudgets.RegistrationPermitLimit - 1];
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await RegisterAsync(client, inbox.Tagged($"after{i}"));
            statusCodes[i] = response.StatusCode;
        }

        // Assert
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.Created);
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

        Dictionary<string, string> form = new()
        {
            ["Username"] = Faker.Random.AlphaNumeric(16),
            ["Email"] = inbox.Dotted,
            ["Password"] = Password,
            ["ConfirmPassword"] = Password,
            ["AcceptedPrivacyPolicy"] = "true",
            ["AcceptedDataProcessingConsent"] = "true",
            ["AcceptedTermsOfService"] = "true"
        };

        // Act
        using HttpResponseMessage refused = await PostToPageAsync(RegisterPage, form);

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
        string[] accounts = [inbox.Tagged("a"), inbox.Dotted, inbox.OnGoogleMail];
        foreach (string account in accounts)
        {
            await RegisterUnconfirmedAsync(account);
        }

        // Act: one more than the budget, spread across the three accounts
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
    public async Task ForgotPasswordPage_ShouldKeepTheOwnersBudget_WhenAStrangersAccountAtHerInboxSpendsItsOwn()
    {
        // Arrange: the page does not go through the endpoint's handler, and it is the door real users use
        GmailInbox inbox = GmailInbox.New();
        await RegisterUnconfirmedAsync(inbox.Plain);
        await UserFactory.ConfirmEmailAsync(ApiClient, AccountConfirmationEmailSpy);
        await RegisterUnconfirmedAsync(inbox.Tagged("stranger"));

        for (int i = 0; i < AccountBudgets.PasswordResetPermitLimit + 1; i++)
        {
            using HttpResponseMessage flood = await PostToPageAsync(
                ForgotPasswordPage, new Dictionary<string, string> { ["Email"] = inbox.Tagged("stranger") });
            flood.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        int rowsAfterFlood = await CountOutboxRowsAsync(nameof(PasswordResetRequested));

        // Act
        for (int i = 0; i < AccountBudgets.PasswordResetPermitLimit; i++)
        {
            using HttpResponseMessage own = await PostToPageAsync(
                ForgotPasswordPage, new Dictionary<string, string> { ["Email"] = inbox.Plain });
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

    private async Task<HttpResponseMessage> RegisterAsync(string email) =>
        await RegisterAsync(ApiClient.Http, email);

    private async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email)
    {
        RegisterRequest request = UserFactory.GenerateRandomRegisterRequest(Faker, Password) with { Email = email };
        return await client.PostAsJsonAsync(RegisterEndpoint, request);
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

    private async Task<HttpResponseMessage> PostToPageAsync(Uri page, Dictionary<string, string> formFields)
    {
        using HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(page);
        pageResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string html = await pageResponse.Content.ReadAsStringAsync();
        Match match = AntiForgeryTokenRegex().Match(html);
        if (match.Success)
        {
            formFields["__RequestVerificationToken"] = match.Groups[1].Value;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, page);
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
    /// Fails the first commit of the registration of one address, with the error code Postgres uses for a
    /// transient serialization failure, so the execution strategy replays it. Every other commit, the
    /// host's own background work included, goes through untouched.
    /// </summary>
    private sealed class FailFirstRegistrationCommitInterceptor : DbTransactionInterceptor
    {
        private readonly string _email;
        private int _failedCommits;

        public FailFirstRegistrationCommitInterceptor(string email)
        {
            _email = email;
        }

        public int FailedCommits => _failedCommits;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            bool isTheRegistration = eventData.Context is not null
                && eventData.Context.ChangeTracker.Entries<ApplicationUser>()
                    .Any(entry => string.Equals(entry.Entity.Email, _email, StringComparison.Ordinal));

            if (isTheRegistration && Interlocked.CompareExchange(ref _failedCommits, 1, 0) == 0)
            {
                throw new PostgresException("simulated transient commit failure", "ERROR", "ERROR", "40001");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

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
