using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Drives the grace-period finalization through its scoped entry point
/// (<see cref="IAccountDeletionFinalizer"/>) — the hosted service merely calls it on a
/// timer — and asserts the observable outcome: anonymized rows and dead logins.
/// </summary>
public sealed class AccountDeletionFinalizerTests : EndpointsTestBase
{
    private const string TestPassword = "TestPass1!";

    public AccountDeletionFinalizerTests(AuthSystemApiFactory appFactory) : base(appFactory) { }

    [Fact]
    public async Task Finalizer_ShouldAnonymizeAccount_WhenGracePeriodElapsed()
    {
        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldStartWith(AnonymizationConstants.EmailPrefix);
        user.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
        user.UserName.ShouldNotBe(registerRequest.Username);
        user.PhoneNumber.ShouldBeNull();
        user.PasswordHash.ShouldBeNull();
        user.EmailConfirmed.ShouldBeFalse();
        user.DataProcessingConsentGiven.ShouldBeFalse();
        user.DataProcessingConsentDate.ShouldBeNull();
        user.PrivacyPolicyAccepted.ShouldBeFalse();
        user.PrivacyPolicyAcceptedDate.ShouldBeNull();
        user.TermsOfServiceAccepted.ShouldBeFalse();
        user.TermsOfServiceAcceptedDate.ShouldBeNull();
        user.LockoutEnabled.ShouldBeTrue();
        user.LockoutEnd.ShouldBe(DateTimeOffset.MaxValue);

        // DeletionScheduledAt stays set as the non-PII audit trace.
        user.DeletionScheduledAt.ShouldNotBeNull();

        // Artifact cleanup removed roles and claims.
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        bool hasRoles = await db.UserRoles.AnyAsync(ur => ur.UserId == identityId.Value);
        hasRoles.ShouldBeFalse();
        bool hasClaims = await db.UserClaims.AnyAsync(uc => uc.UserId == identityId.Value);
        hasClaims.ShouldBeFalse();

        HttpResponseMessage loginResponse = await RequestTokenAsync(registerRequest.Email, TestPassword);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Finalizer_ShouldNotTouchAccount_BeforeGracePeriodElapses()
    {
        // Arrange — freshly scheduled, still well inside the 14-day window
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(0);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(registerRequest.Email);
        user.DeletionScheduledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Finalizer_ShouldBeIdempotent_WhenRunRepeatedly()
    {
        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        // Act
        int firstRunCount = await RunFinalizerAsync();
        int secondRunCount = await RunFinalizerAsync();

        // Assert — the second run sees the anonymization marker and skips the account
        firstRunCount.ShouldBe(1);
        secondRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Finalizer_ShouldIgnoreAccounts_WithoutScheduledDeletion()
    {
        // Arrange — a healthy account, never scheduled
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(0);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(registerRequest.Email);
        user.DeletionScheduledAt.ShouldBeNull();
    }

    private async Task<int> RunFinalizerAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IAccountDeletionFinalizer finalizer =
            scope.ServiceProvider.GetRequiredService<IAccountDeletionFinalizer>();
        return await finalizer.FinalizeDueAccountsAsync(CancellationToken.None);
    }

    private async Task BackdateScheduleAsync(Guid userId, TimeSpan age)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ApplicationUser user = await db.Users.FirstAsync(u => u.Id == userId);
        user.DeletionScheduledAt = DateTimeOffset.UtcNow - age;
        await db.SaveChangesAsync();
    }

    private async Task<(RegisterRequest Request, IdentityId IdentityId)> RegisterAndScheduleDeletionAsync()
    {
        (RegisterRequest registerRequest, IdentityId identityId) =
            await UserFactory.RegisterRandomUserWithRequestAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);

        string accessToken = await GetAccessTokenAsync(registerRequest.Email, TestPassword);

        DeleteAccountRequest deleteRequest = new(TestPassword);
        using HttpRequestMessage request = new(HttpMethod.Post, "auth/account/delete");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = JsonContent.Create(deleteRequest);

        HttpResponseMessage response = await ApiClient.Http.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        return (registerRequest, identityId);
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
