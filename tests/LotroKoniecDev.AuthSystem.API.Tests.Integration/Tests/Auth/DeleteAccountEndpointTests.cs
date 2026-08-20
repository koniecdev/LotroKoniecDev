using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Outbox;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

public sealed class DeleteAccountEndpointTests : EndpointsTestBase
{
    private const string TestPassword = "TestPass1!";

    public DeleteAccountEndpointTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task DeleteAccount_ShouldReturnNoContentWithSchedulingHeaders_WhenPasswordIsCorrect()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act
        HttpResponseMessage response = await SendDeleteRequestAsync(accessToken, TestPassword);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        response.Headers.TryGetValues(DeleteAccount.DeletionScheduledAtHeader, out IEnumerable<string>? scheduledAtValues)
            .ShouldBeTrue($"the {DeleteAccount.DeletionScheduledAtHeader} header must be present");
        response.Headers.TryGetValues(DeleteAccount.DeletionFinalizesAtHeader, out IEnumerable<string>? finalizesAtValues)
            .ShouldBeTrue($"the {DeleteAccount.DeletionFinalizesAtHeader} header must be present");

        string? scheduledAtHeader = scheduledAtValues!.SingleOrDefault();
        string? finalizesAtHeader = finalizesAtValues!.SingleOrDefault();

        scheduledAtHeader.ShouldNotBeNullOrWhiteSpace();
        finalizesAtHeader.ShouldNotBeNullOrWhiteSpace();

        DateTimeOffset scheduledAt = DateTimeOffset.Parse(scheduledAtHeader, System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset finalizesAt = DateTimeOffset.Parse(finalizesAtHeader, System.Globalization.CultureInfo.InvariantCulture);
        (finalizesAt - scheduledAt).ShouldBe(TimeSpan.FromDays(14));
    }

    [Fact]
    public async Task DeleteAccount_ShouldScheduleDeletionAndLockAccount_WithoutAnonymizingData()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act
        HttpResponseMessage response = await SendDeleteRequestAsync(accessToken, TestPassword);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        ApplicationUser user = await GetUserAsync(identityId.Value);

        user.DeletionScheduledAt.ShouldNotBeNull();
        user.LockoutEnabled.ShouldBeTrue();
        user.LockoutEnd.ShouldBe(user.DeletionScheduledAt.Value.AddDays(14));

        // Data stays intact during the grace window — only the finalizer anonymizes.
        user.Email.ShouldBe(registerRequest.Email);
        user.UserName.ShouldBe(registerRequest.Username);
        user.PasswordHash.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteAccount_ShouldDeliverCancellationEmail_WhenScheduling()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act: the request only commits the outbox row; the e-mail arrives through the
        // pipeline (relay -> delivery -> spy), so the capture has to be awaited (ADR-0038)
        HttpResponseMessage response = await SendDeleteRequestAsync(accessToken, TestPassword);
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync();

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AccountDeletionEmailSpy.ScheduledCallCount.ShouldBe(1);
        AccountDeletionEmailSpy.LastScheduledEmail.ShouldBe(registerRequest.Email);
        AccountDeletionEmailSpy.LastCancelToken.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DeleteAccount_ShouldWriteAnIdOnlyOutboxRow_WhenScheduling()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act
        HttpResponseMessage response = await SendDeleteRequestAsync(accessToken, TestPassword);
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync();

        // Assert: the payload carries the user id and nothing else: the cancel token is minted
        // at delivery and must never persist in an outbox row (ADR-0038 decision 2)
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
            Factory, row => row.Type == nameof(AccountDeletionScheduled));
        outboxRow.ShouldNotBeNull();
        AccountDeletionScheduled payload = JsonSerializer.Deserialize<AccountDeletionScheduled>(outboxRow.Payload)
            .ShouldNotBeNull();
        payload.IdentityUserId.ShouldBe(identityId.Value);
        AccountDeletionEmailSpy.LastCancelToken.ShouldNotBeNullOrEmpty();
        outboxRow.Payload.ShouldNotContain(AccountDeletionEmailSpy.LastCancelToken);
    }

    [Fact]
    public async Task DeleteAccount_ShouldPreventLogin_DuringGraceWindow()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);
        await SendDeleteRequestAsync(accessToken, TestPassword);

        // Act: try to login with the (still correct) credentials
        HttpResponseMessage loginResponse = await RequestTokenAsync(registerRequest.Email, TestPassword);

        // Assert: the dedicated error lets clients show the "scheduled for deletion" state
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await loginResponse.Content.ReadAsStringAsync();
        body.ShouldContain("account_deletion_scheduled");
    }

    [Fact]
    public async Task DeleteAccount_ShouldReturnGenericLoginError_WhenPasswordIsWrongDuringGraceWindow()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);
        await SendDeleteRequestAsync(accessToken, TestPassword);

        // Act: wrong password must not reveal that deletion is scheduled
        HttpResponseMessage loginResponse = await RequestTokenAsync(registerRequest.Email, "WrongPassword1!");

        // Assert
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await loginResponse.Content.ReadAsStringAsync();
        body.ShouldNotContain("account_deletion_scheduled");
    }

    [Fact]
    public async Task DeleteAccount_ShouldReturnUnprocessableEntity_WhenAlreadyScheduled()
    {
        // Arrange
        (RegisterRequest registerRequest, _) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);
        HttpResponseMessage firstResponse = await SendDeleteRequestAsync(accessToken, TestPassword);
        firstResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync();

        // Act: the JWT is self-contained, so it stays usable within its lifetime
        HttpResponseMessage secondResponse = await SendDeleteRequestAsync(accessToken, TestPassword);

        // Assert: the rejected retry must not have queued a second e-mail
        secondResponse.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        string body = await secondResponse.Content.ReadAsStringAsync();
        body.ShouldContain("Auth.DeletionAlreadyScheduled");
        AccountDeletionEmailSpy.ScheduledCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAccount_ShouldReturnBadRequest_WhenPasswordIsWrong()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        // Act
        HttpResponseMessage response = await SendDeleteRequestAsync(accessToken, "WrongPassword1!");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.DeletionScheduledAt.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAccount_ShouldReturnUnauthorized_WhenNotAuthenticated()
    {
        // Arrange
        DeleteAccountRequest deleteRequest = new(TestPassword);

        // Act
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Content = JsonContent.Create(deleteRequest);
        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteAccount_ShouldKeepScheduleAndSucceed_WhenCancellationEmailFails()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        AccountDeletionEmailSpy.ShouldFailScheduledEmail = true;

        try
        {
            // Act
            HttpResponseMessage response = await SendDeleteRequestAsync(accessToken, TestPassword);
            await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync(TimeSpan.FromSeconds(5));

            // Assert: the unwind compensation is gone (ADR-0038 decision 5): the request only
            // commits the outbox row atomically with the schedule, so an SMTP failure neither
            // fails the request nor unwinds the schedule — redelivery (or a DLQ replay) owns it.
            response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

            ApplicationUser user = await GetUserAsync(identityId.Value);
            user.DeletionScheduledAt.ShouldNotBeNull();

            OutboxMessage? outboxRow = await OutboxAssertions.WaitForOutboxRowAsync(
                Factory, row => row.Type == nameof(AccountDeletionScheduled));
            outboxRow.ShouldNotBeNull();
        }
        finally
        {
            AccountDeletionEmailSpy.ShouldFailScheduledEmail = false;
        }
    }

    private async Task<HttpResponseMessage> SendDeleteRequestAsync(string accessToken, string password)
    {
        DeleteAccountRequest deleteRequest = new(password);

        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(deleteRequest);

        return await ApiClient.Http.SendAsync(request);
    }

    private async Task<HttpResponseMessage> RequestTokenAsync(string email, string password)
    {
        using FormUrlEncodedContent tokenRequest = new(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = password,
            ["client_id"] = "lotrokoniecdev-test",
            ["scope"] = "email profile roles api"
        });

        return await ApiClient.Http.PostAsync(new Uri("connect/token", UriKind.Relative), tokenRequest);
    }

    private async Task<ApplicationUser> GetUserAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
    }
}
