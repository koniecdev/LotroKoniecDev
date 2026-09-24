using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Password;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.RateLimiting;

/// <summary>
/// The budget of deletion schedules that belongs to the account (#811). Cancelling a deletion returns a
/// reset token in its response, so schedule, cancel, reset and log in is a loop that needs only the cancel
/// link in the account's current inbox, and no fresh IP. Every schedule mails the account, and the address
/// an armed undo would restore as well.
/// These tests run on the suite's Testing host, where the limiter middleware is off, so the only things
/// that can refuse a request are the budgets in the handler.
/// </summary>
public sealed class DeletionScheduleBudgetTests : EndpointsTestBase
{
    /// <summary>The shipped schedule budget, read from the same constant the registration uses.</summary>
    private const int PermitLimit = AccountBudgets.DeletionSchedulePermitLimit;

    private const string Password = "TestPass1!";
    private const string WrongPassword = "Wrong-Password1!";
    private const string ThrottledErrorCode = "Auth.DeletionScheduleThrottled";
    private const string DeletePath = "auth/account/delete";

    public DeletionScheduleBudgetTests(AuthSystemApiFactory appFactory) : base(appFactory)
    {
    }

    [Fact]
    public async Task DeleteAccount_ShouldScheduleAgainAfterACancel_AndRefuseTheScheduleAfterTheBudget()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        // Act: schedule and cancel as many times as the budget allows, the way a person who changes
        // their mind would, and as a loop would
        HttpStatusCode[] schedules = new HttpStatusCode[PermitLimit];
        for (int i = 0; i < schedules.Length; i++)
        {
            schedules[i] = await ScheduleAsync(accessToken, Password);
            accessToken = await CancelAndLogInAgainAsync(registerRequest.Email);
        }

        using HttpResponseMessage refused = await PostDeleteAsync(accessToken, Password);

        // Assert
        schedules.ShouldAllBe(statusCode => statusCode == HttpStatusCode.NoContent);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadErrorCodeAsync(refused)).ShouldBe(ThrottledErrorCode);

        // Nothing was scheduled and the account stays usable: the refusal came before the save
        ApplicationUser user = await LoadUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldBeNull();
        user.LockoutEnd.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAccount_ShouldNotSpendTheScheduleBudget_WhenThePasswordIsWrong()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        HttpStatusCode[] typos = new HttpStatusCode[PermitLimit + 1];
        for (int i = 0; i < typos.Length; i++)
        {
            using HttpResponseMessage typo = await PostDeleteAsync(accessToken, WrongPassword);
            typos[i] = typo.StatusCode;
        }

        // Act
        using HttpResponseMessage response = await PostDeleteAsync(accessToken, Password);

        // Assert: typos are the confirmation budget's business; a schedule permit pays only for a schedule
        typos.ShouldAllBe(statusCode => statusCode == HttpStatusCode.BadRequest);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        ApplicationUser user = await LoadUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteAccount_ShouldNotSpendTheScheduleBudget_WhenTheDeletionIsAlreadyScheduled()
    {
        // Arrange: one permit goes on the first schedule. An access token already issued keeps working
        // after it, so the same caller can ask again while the deletion is pending.
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, Password);
        string accessToken = await GetAccessTokenAsync(registerRequest.Email, Password);

        (await ScheduleAsync(accessToken, Password)).ShouldBe(HttpStatusCode.NoContent);

        HttpStatusCode[] repeats = new HttpStatusCode[PermitLimit];
        for (int i = 0; i < repeats.Length; i++)
        {
            using HttpResponseMessage repeat = await PostDeleteAsync(accessToken, Password);
            repeats[i] = repeat.StatusCode;
        }

        string freshToken = await CancelAndLogInAgainAsync(registerRequest.Email);

        // Act
        HttpStatusCode reschedule = await ScheduleAsync(freshToken, Password);

        // Assert: the repeats changed nothing, so the budget still holds the second permit
        repeats.ShouldAllBe(statusCode => statusCode == HttpStatusCode.UnprocessableEntity);
        reschedule.ShouldBe(HttpStatusCode.NoContent);

        ApplicationUser user = await LoadUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldNotBeNull();
    }

    /// <summary>
    /// Schedules the deletion and waits for the cancel link. The link is minted when the mail is
    /// delivered through the outbox, not in the request, so the spy is cleared first and then awaited.
    /// </summary>
    private async Task<HttpStatusCode> ScheduleAsync(string accessToken, string password)
    {
        AccountDeletionEmailSpy.Reset();

        using HttpResponseMessage response = await PostDeleteAsync(accessToken, password);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync();
            AccountDeletionEmailSpy.LastCancelToken.ShouldNotBeNullOrWhiteSpace();
        }

        return response.StatusCode;
    }

    /// <summary>
    /// Walks the recovery a cancel forces: the cancel wipes the password and answers with a reset token,
    /// the reset sets the same password again, and a new login gives a new access token.
    /// </summary>
    private async Task<string> CancelAndLogInAgainAsync(string email)
    {
        CancelAccountDeletionRequest cancelRequest = new(email, AccountDeletionEmailSpy.LastCancelToken!);
        using HttpResponseMessage cancelResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative), cancelRequest);
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        CancelAccountDeletionResponse cancelBody = (await cancelResponse.Content
            .ReadFromJsonAsync<CancelAccountDeletionResponse>(ApiClient.JsonOptions))!;

        ResetPasswordRequest resetRequest = new(email, cancelBody.PasswordResetToken, Password);
        using HttpResponseMessage resetResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/reset-password", UriKind.Relative), resetRequest);
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await GetAccessTokenAsync(email, Password);
    }

    private async Task<HttpResponseMessage> PostDeleteAsync(string accessToken, string password)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, DeletePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(new DeleteAccountRequest(password));
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

    private async Task<ApplicationUser> LoadUserAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Users.AsNoTracking().FirstAsync(row => row.Id == userId);
    }
}
