using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The send budget that follows the inbox the mail would reach, not the caller asking for it (#692). The
/// IP policies cannot see it: an attacker who rotates IPs gets a fresh budget every time and the victim
/// gets every mail. The budget is keyed by the account id, so two spellings of one address cannot split
/// it — these tests drive it the way a caller does, by typing an address.
/// They run on the suite's normal Testing host, where the limiter middleware is off, so the only thing
/// that can refuse a request here is this budget and nothing else can be mistaken for it.
/// </summary>
public sealed partial class PasswordResetAddressBudgetTests : EndpointsTestBase
{
    /// <summary>Mirrors PasswordResetRequestThrottle: 3 sends per 15 minutes per account.</summary>
    private const int AddressPermitLimit = 3;

    private static readonly Uri ForgotPasswordPage = new("/Account/ForgotPassword", UriKind.Relative);
    private static readonly Uri ForgotPasswordEndpoint = new("auth/forgot-password", UriKind.Relative);

    public PasswordResetAddressBudgetTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldStopQueueingAfterTheAddressBudget()
    {
        // Arrange
        (RegisterRequest request, IdentityId _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act: one more than the budget, all from the same client
        HttpStatusCode[] statusCodes = new HttpStatusCode[AddressPermitLimit + 1];
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await PostToForgotPasswordPageAsync(request.Email);
            statusCodes[i] = response.StatusCode;
        }

        // Assert: the budget bounds what was queued, and the page never says which request was refused
        int queuedRows = await CountPasswordResetRowsAsync();
        queuedRows.ShouldBe(AddressPermitLimit);
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldKeepShowingTheNeutralPanel_WhenTheAddressBudgetIsSpent()
    {
        // Arrange: a visible "you already asked" message would tell a caller that somebody else had just
        // requested a reset for this address, which is the leak ADR-0038 decision 5 exists to avoid
        (RegisterRequest request, IdentityId _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        for (int i = 0; i < AddressPermitLimit; i++)
        {
            using HttpResponseMessage spend = await PostToForgotPasswordPageAsync(request.Email);
            spend.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act
        using HttpResponseMessage refused = await PostToForgotPasswordPageAsync(request.Email);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await refused.Content.ReadAsStringAsync();
        html.ShouldContain("Jeśli konto istnieje");
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldNotSpendTheBudgetOfAnotherAddress()
    {
        // Arrange: flooding one inbox must not lock every other user out of password reset
        (RegisterRequest floodedUser, IdentityId _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);
        (RegisterRequest otherUser, IdentityId _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        for (int i = 0; i < AddressPermitLimit + 1; i++)
        {
            using HttpResponseMessage flood = await PostToForgotPasswordPageAsync(floodedUser.Email);
            flood.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        int rowsAfterFlood = await CountPasswordResetRowsAsync();

        // Act
        using HttpResponseMessage other = await PostToForgotPasswordPageAsync(otherUser.Email);

        // Assert
        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        int rowsAfterOther = await CountPasswordResetRowsAsync();
        rowsAfterOther.ShouldBe(rowsAfterFlood + 1);
    }

    [Fact]
    public async Task ForgotPasswordPage_ShouldNotSpendTheBudgetOnAnAddressNobodyRegistered()
    {
        // Arrange: the budget counts sends, so an address with no account must never consume one —
        // otherwise a stranger could burn a victim's budget before the victim ever asks
        string unregistered = $"nobody-{Guid.CreateVersion7()}@example.com";

        // Act
        for (int i = 0; i < AddressPermitLimit + 2; i++)
        {
            using HttpResponseMessage response = await PostToForgotPasswordPageAsync(unregistered);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Assert
        int queuedRows = await CountPasswordResetRowsAsync();
        queuedRows.ShouldBe(0);
    }

    [Fact]
    public async Task ForgotPasswordEndpoint_ShouldStopQueueingAfterTheAddressBudget()
    {
        // Arrange: the API twin has no caller in the product, but it is public — so without its own
        // budget it is simply the way around the page's
        (RegisterRequest request, IdentityId _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy);

        // Act
        HttpStatusCode[] statusCodes = new HttpStatusCode[AddressPermitLimit + 1];
        for (int i = 0; i < statusCodes.Length; i++)
        {
            using HttpResponseMessage response = await ApiClient.Http.PostAsJsonAsync(
                ForgotPasswordEndpoint, new ForgotPasswordRequest(request.Email));
            statusCodes[i] = response.StatusCode;
        }

        // Assert: the refusal answers Success like every other branch, so it reveals nothing
        int queuedRows = await CountPasswordResetRowsAsync();
        queuedRows.ShouldBe(AddressPermitLimit);
        statusCodes.ShouldAllBe(statusCode => statusCode == HttpStatusCode.OK);
    }

    private async Task<int> CountPasswordResetRowsAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        return await db.OutboxMessages
            .Where(row => row.Type == nameof(PasswordResetRequested))
            .CountAsync();
    }

    private async Task<HttpResponseMessage> PostToForgotPasswordPageAsync(string email)
    {
        HttpResponseMessage pageResponse = await ApiClient.Http.GetAsync(ForgotPasswordPage);
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
        using HttpRequestMessage request = new(HttpMethod.Post, ForgotPasswordPage);
        request.Content = content;

        if (pageResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies))
        {
            foreach (string cookie in cookies)
            {
                request.Headers.Add("Cookie", cookie.Split(';')[0]);
            }
        }

        pageResponse.Dispose();
        return await ApiClient.Http.SendAsync(request);
    }

    [GeneratedRegex("""name="__RequestVerificationToken".*?value="([^"]+)""")]
    private static partial Regex AntiForgeryTokenRegex();
}
