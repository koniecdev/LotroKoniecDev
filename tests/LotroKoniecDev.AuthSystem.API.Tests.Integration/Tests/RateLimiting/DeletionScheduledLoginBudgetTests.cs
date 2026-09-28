using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The login budget of an account with a scheduled deletion (#881). The deletion locks the account for the
/// whole grace period, so the login page checks it before the lockout, and Identity's lockout never slows
/// guessing there. The budget belongs to the account, so a guesser who changes address keeps spending it.
/// These tests run on the suite's Testing host, where the limiter middleware is off, so the page limit per
/// address never refuses anything and only the budget can. In Testing <c>UseForwardedHeaders</c> trusts
/// every peer, so <c>X-Forwarded-For</c> plays the visitor's address.
/// </summary>
public sealed partial class DeletionScheduledLoginBudgetTests : EndpointsTestBase
{
    /// <summary>The shipped budget, read from the same constant the registration uses.</summary>
    private const int PermitLimit = AccountBudgets.DeletionScheduledLoginPermitLimit;

    private const string Password = "TestPass1!";
    private const string WrongPassword = "Wrong-Password1!";
    private const string GeneralMessage = "Nieprawidłowy e-mail lub hasło.";
    private const string DeletionScheduledMessage = "Twoje konto jest zaplanowane do usunięcia";
    private const string ForwardedForHeader = "X-Forwarded-For";

    /// <summary>Visitor addresses from the ranges kept for documentation (RFC 5737).</summary>
    private const string FreshAddress = "198.51.100.7";
    private const string SharedAddress = "203.0.113.50";
    private const string ReturningAddress = "203.0.113.60";

    public DeletionScheduledLoginBudgetTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task LoginPage_ShouldAnswerTheRightPasswordWithTheGeneralMessage_OnceTheBudgetIsSpentFromManyAddresses()
    {
        // Arrange: every guess comes from a new address, the way a guesser who rotates addresses sends them
        string email = await RegisterWithScheduledDeletionAsync();

        string?[] guesses = new string?[PermitLimit];
        for (int i = 0; i < guesses.Length; i++)
        {
            guesses[i] = await PostAndReadAlertAsync(email, WrongPassword, GuesserAddress(i));
        }

        // Act: the right password, from an address that has sent nothing yet
        string? refused = await PostAndReadAlertAsync(email, Password, FreshAddress);

        // Assert
        guesses.ShouldAllBe(message => message == GeneralMessage);
        refused.ShouldBe(GeneralMessage);
    }

    [Fact]
    public async Task LoginPage_ShouldStillNameTheScheduledDeletion_OnTheLastPermit()
    {
        // Arrange
        string email = await RegisterWithScheduledDeletionAsync();

        string?[] guesses = new string?[PermitLimit - 1];
        for (int i = 0; i < guesses.Length; i++)
        {
            guesses[i] = await PostAndReadAlertAsync(email, WrongPassword, GuesserAddress(i));
        }

        // Act
        string? lastPermit = await PostAndReadAlertAsync(email, Password, FreshAddress);

        // Assert: the owner who types the right password inside the budget still learns the deletion date
        guesses.ShouldAllBe(message => message == GeneralMessage);
        lastPermit.ShouldStartWith(DeletionScheduledMessage);
    }

    [Fact]
    public async Task LoginPage_ShouldKeepAnotherAccountsBudget_WhenOneAccountsBudgetIsSpentFromTheSameAddress()
    {
        // Arrange
        string spent = await RegisterWithScheduledDeletionAsync();
        string bystander = await RegisterWithScheduledDeletionAsync();

        string?[] guesses = new string?[PermitLimit];
        for (int i = 0; i < guesses.Length; i++)
        {
            guesses[i] = await PostAndReadAlertAsync(spent, WrongPassword, SharedAddress);
        }

        // Act
        string? bystanderAnswer = await PostAndReadAlertAsync(bystander, Password, SharedAddress);

        // Assert: the budget is not a limit on the address, nor on everyone
        guesses.ShouldAllBe(message => message == GeneralMessage);
        bystanderAnswer.ShouldStartWith(DeletionScheduledMessage);
    }

    [Fact]
    public async Task LoginPage_ShouldNotSpendTheBudget_WhileNoDeletionIsScheduled()
    {
        // Arrange: more successful logins than the budget holds, before the deletion is scheduled
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);

        HttpStatusCode[] logins = new HttpStatusCode[PermitLimit + 1];
        for (int i = 0; i < logins.Length; i++)
        {
            using HttpResponseMessage login = await PostToLoginPageAsync(registerRequest.Email, Password, ReturningAddress);
            logins[i] = login.StatusCode;
        }

        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, registerRequest.Email);

        // Act
        string? answer = await PostAndReadAlertAsync(registerRequest.Email, Password, ReturningAddress);

        // Assert: the budget brakes only the branch the lockout cannot reach
        logins.ShouldAllBe(statusCode => statusCode == HttpStatusCode.Found);
        answer.ShouldStartWith(DeletionScheduledMessage);
    }

    [Fact]
    public async Task DownloadAccountData_ShouldStillServeTheOwner_WhenAStrangerSpentTheLoginBudget()
    {
        // Arrange: the owner schedules the deletion and keeps the access token they already hold, which works
        // until it expires. The export is served during the grace period on purpose (ADR-0052), and a
        // stranger on the login page must not use up its budget.
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        AccountDeletionEmailSpy.Reset();
        using HttpResponseMessage scheduled =
            await PostWithTokenAsync("auth/account/delete", accessToken, new DeleteAccountRequest(Password));
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync();

        string?[] guesses = new string?[PermitLimit + 1];
        for (int i = 0; i < guesses.Length; i++)
        {
            guesses[i] = await PostAndReadAlertAsync(registerRequest.Email, WrongPassword, GuesserAddress(i));
        }

        // Act
        using HttpResponseMessage response =
            await PostWithTokenAsync("auth/account/data-export", accessToken, new DownloadAccountDataRequest(Password));

        // Assert
        scheduled.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        guesses.ShouldAllBe(message => message == GeneralMessage);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static string GuesserAddress(int guess) => $"203.0.113.{guess + 1}";

    private async Task<string> RegisterWithScheduledDeletionAsync()
    {
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        await AccountStateFactory.ScheduleDeletionAsync(Factory.Services, registerRequest.Email);
        return registerRequest.Email;
    }

    /// <summary>
    /// Returns the message the page shows, or null when it shows none: a redirect, a refused POST, or a page
    /// without the alert all read as null, so the test's own assert catches them.
    /// </summary>
    private async Task<string?> PostAndReadAlertAsync(string email, string password, string visitorAddress)
    {
        using HttpResponseMessage response = await PostToLoginPageAsync(email, password, visitorAddress);

        Match match = AlertMessageRegex().Match(await response.Content.ReadAsStringAsync());
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// Fetches the page for its antiforgery token and cookie on the same client, then posts the credentials
    /// as if they came from <paramref name="visitorAddress"/>.
    /// </summary>
    private async Task<HttpResponseMessage> PostToLoginPageAsync(string email, string password, string visitorAddress)
    {
        using HttpClient browser = Factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage pageResponse = await browser.GetAsync(new Uri("/Account/Login", UriKind.Relative));

        Dictionary<string, string> formFields = new()
        {
            ["Email"] = email,
            ["Password"] = password
        };
        Match antiForgeryToken = AntiForgeryTokenRegex().Match(await pageResponse.Content.ReadAsStringAsync());
        if (antiForgeryToken.Success)
        {
            formFields["__RequestVerificationToken"] = antiForgeryToken.Groups[1].Value;
        }

        using FormUrlEncodedContent content = new(formFields);
        using HttpRequestMessage request = new(HttpMethod.Post, "/Account/Login");
        request.Headers.Add(ForwardedForHeader, visitorAddress);
        request.Content = content;

        return await browser.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostWithTokenAsync(string path, string accessToken, object body)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(body, body.GetType());
        return await ApiClient.Http.SendAsync(request);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();

    [GeneratedRegex("""<span class="s">(.*?)</span>""", RegexOptions.Singleline)]
    private static partial Regex AlertMessageRegex();
}
