using System.Data.Common;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.SharedKernel.Constants;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.Tests.Shared;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Tests.Auth;

/// <summary>
/// Runs the finalization that happens after the grace period through its own entry point,
/// <see cref="IAccountDeletionFinalizer"/>, which is all the hosted service does on its timer, and
/// checks the result: anonymized rows and logins that no longer work. A race the finalizer cannot be
/// stopped in the middle of goes through <see cref="IAccountErasureService"/> instead.
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
    public async Task Finalizer_ShouldClearTheArmedRevertTarget_WhenItAnonymizesTheAccount()
    {
        // The armed target is a former address of this person, so erasure has to take it with the
        // rest. It also releases the reservation of #684 — an erased account must not keep somebody
        // else's address blocked.
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await ArmRevertTargetAsync(identityId.Value, "poprzedni@shire.me", TimeSpan.FromDays(15));
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        await RunFinalizerAsync();

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.EmailChangeRevertTo.ShouldBeNull();
        user.NormalizedEmailChangeRevertTo.ShouldBeNull();
        user.EmailChangeRevertArmedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Finalizer_ShouldNotTouchAccount_WhileAnArmedUndoIsStillLive()
    {
        // #685's invariant: no path erases an account while a live undo link for it exists, because
        // following that link cancels the deletion. The row is written by hand because the flows
        // cannot produce it — a scheduled deletion refuses both e-mail-change legs, so arming always
        // comes first — and the guard has to hold whatever order the two timestamps arrive in.
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(20));
        await ArmRevertTargetAsync(identityId.Value, "poprzedni@shire.me", TimeSpan.FromDays(1));

        int finalizedCount = await RunFinalizerAsync();

        finalizedCount.ShouldBe(0);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(registerRequest.Email);
        user.DeletionScheduledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Finalizer_ShouldAnonymizeAccount_OnceTheHeldUndoWindowHasExpired()
    {
        // The other half of the hold: it releases. An undo that can no longer be used stops protecting
        // anything, and the deletion the owner never cancelled goes through.
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(20));
        await ArmRevertTargetAsync(identityId.Value, "poprzedni@shire.me", TimeSpan.FromDays(1));

        int heldRunCount = await RunFinalizerAsync();

        await ArmRevertTargetAsync(identityId.Value, "poprzedni@shire.me", TimeSpan.FromDays(15));
        int releasedRunCount = await RunFinalizerAsync();

        heldRunCount.ShouldBe(0);
        releasedRunCount.ShouldBe(1);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldStartWith(AnonymizationConstants.EmailPrefix);
    }

    [Fact]
    public async Task Finalizer_ShouldNotTouchAccount_BeforeGracePeriodElapses()
    {
        // Arrange: freshly scheduled, still well inside the 14-day window
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

        // Assert: the second run sees the anonymization marker and skips the account
        firstRunCount.ShouldBe(1);
        secondRunCount.ShouldBe(0);
    }

    [Fact]
    public async Task Finalizer_ShouldIgnoreAccounts_WithoutScheduledDeletion()
    {
        // Arrange: a healthy account, never scheduled
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

    [Fact]
    public async Task Finalizer_ShouldChangeTheSecurityStampInTheErasureSave_WhenTheNextSaveOfTheAccountFails()
    {
        // #908: the erasure saves the account once, and the lockout and the new security stamp are
        // part of that save. The next save of this account belongs to the cleanup, which is best
        // effort, so its failure must not turn a finished erasure into a failed one.

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        string? stampBeforeErasure = (await GetUserAsync(identityId.Value)).SecurityStamp;
        FailASecondSaveOf(identityId.Value);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
        user.LockoutEnd.ShouldBe(DateTimeOffset.MaxValue);
        user.SecurityStamp.ShouldNotBe(stampBeforeErasure);
    }

    [Theory]
    [InlineData(ErasureSaveFailure.DatabaseError)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite)]
    public async Task Finalizer_ShouldFinishTheErasureOnItsNextRun_WhenTheErasureSaveFailed(ErasureSaveFailure failure)
    {
        // The finalizer finds its work by the real address, so a failed erasure has to leave it in
        // place for the next run to pick the account up again (#908).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        FailTheNextSaveOf(identityId.Value, failure);

        // Act
        int failedRunCount = await RunFinalizerAsync();
        int retryRunCount = await RunFinalizerAsync();

        // Assert
        failedRunCount.ShouldBe(0);
        retryRunCount.ShouldBe(1);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
        (await HasRolesAsync(identityId.Value)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ErasureSaveFailure.DatabaseError)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite)]
    public async Task Finalizer_ShouldLockTheAccountForGoodAndKeepItsAddress_WhenTheErasureSaveFailed(ErasureSaveFailure failure)
    {
        // The failed save leaves the anonymized values on the tracked account. The emergency lock has
        // to land anyway, and it must not write them: the next run finds the account by its real
        // address (#937). It keeps the security stamp, so a cancel link the owner still holds works.

        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        string? stampBeforeErasure = (await GetUserAsync(identityId.Value)).SecurityStamp;
        FailTheNextSaveOf(identityId.Value, failure);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory);

        // Assert
        finalizedCount.ShouldBe(0);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(registerRequest.Email);
        user.LockoutEnabled.ShouldBeTrue();
        user.LockoutEnd.ShouldBe(DateTimeOffset.MaxValue);
        user.SecurityStamp.ShouldBe(stampBeforeErasure);

        CapturingLoggerFactory.LogEntry lockout = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockout)
            .ShouldHaveSingleItem();
        lockout.Message.ShouldContain(identityId.Value.ToString());
    }

    [Theory]
    [InlineData(ErasureSaveFailure.DatabaseError)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite)]
    public async Task Finalizer_ShouldEraseTheOtherDueAccounts_WhenOneErasureSaveFailed(ErasureSaveFailure failure)
    {
        // The failing account has waited longest, so the finalizer takes it first and every later save
        // in the run comes after its failure. None of them may carry its unsaved changes (#937).

        // Arrange
        (RegisterRequest failingRequest, IdentityId failingId) = await RegisterAndScheduleDeletionAsync();
        (_, IdentityId otherId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(failingId.Value, TimeSpan.FromDays(20));
        await BackdateScheduleAsync(otherId.Value, TimeSpan.FromDays(15));
        FailTheNextSaveOf(failingId.Value, failure);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);

        ApplicationUser other = await GetUserAsync(otherId.Value);
        other.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
        (await HasRolesAsync(otherId.Value)).ShouldBeFalse();

        ApplicationUser failing = await GetUserAsync(failingId.Value);
        failing.Email.ShouldBe(failingRequest.Email);
        failing.LockoutEnd.ShouldBe(DateTimeOffset.MaxValue);
    }

    [Fact]
    public async Task Finalizer_ShouldSkipAnAccount_WhenItsOwnerCancelledTheDeletionAfterTheRunListedIt()
    {
        // The cancel link still works for a while after the account becomes due, so a run can list an
        // account whose owner cancels before its turn comes. The finalizer reads every account again,
        // with the rule that listed it, right before its erasure. A read without that rule would hand
        // the cancelled account to the erasure with its new stamp, and the erasure would win.

        // Arrange
        (_, IdentityId firstId) = await RegisterAndScheduleDeletionAsync();
        (RegisterRequest cancellingRequest, IdentityId cancellingId) = await RegisterAndScheduleDeletionAsync();
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync(cancellingRequest.Email);
        string cancelToken = AccountDeletionEmailSpy.LastCancelTokenSentTo(cancellingRequest.Email)!;
        await BackdateScheduleAsync(firstId.Value, TimeSpan.FromDays(20));
        await BackdateScheduleAsync(cancellingId.Value, TimeSpan.FromDays(15));
        HttpStatusCode? cancelStatus = null;

        // Act
        (int finalizedCount, IReadOnlyList<Guid> handedOver) = await RunFinalizerAsync(beforeFirstErasure: async () =>
        {
            HttpResponseMessage cancelResponse = await ApiClient.Http.PostAsJsonAsync(
                new Uri("auth/account/cancel-deletion", UriKind.Relative),
                new CancelAccountDeletionRequest(cancellingRequest.Email, cancelToken));
            cancelStatus = cancelResponse.StatusCode;
        });

        // Assert
        cancelStatus.ShouldBe(HttpStatusCode.OK);
        finalizedCount.ShouldBe(1);
        handedOver.ShouldBe([firstId.Value]);

        ApplicationUser cancelled = await GetUserAsync(cancellingId.Value);
        cancelled.Email.ShouldBe(cancellingRequest.Email);
        cancelled.DeletionScheduledAt.ShouldBeNull();
        cancelled.LockoutEnd.ShouldBeNull();

        ApplicationUser first = await GetUserAsync(firstId.Value);
        first.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
    }

    [Fact]
    public async Task Erasure_ShouldLeaveTheAccountUnlocked_WhenItsOwnerCancelledTheDeletionMeanwhile()
    {
        // The erasure read the account, then the owner followed the cancel link, so the erasure save
        // loses on the concurrency stamp. A lock after that would shut the owner out for good, with no
        // cancel link left to undo it.

        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync(registerRequest.Email);
        string cancelToken = AccountDeletionEmailSpy.LastCancelTokenSentTo(registerRequest.Email)!;

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ApplicationUser readBeforeTheCancel = await db.Users.SingleAsync(row => row.Id == identityId.Value);

        HttpResponseMessage cancelResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative),
            new CancelAccountDeletionRequest(registerRequest.Email, cancelToken));
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using CapturingLoggerFactory loggerFactory = new();
        IAccountErasureService erasureService = CreateErasureService(scope.ServiceProvider, loggerFactory);

        // Act
        Result erasureResult = await erasureService.EraseAsync(readBeforeTheCancel, CancellationToken.None);

        // Assert
        erasureResult.IsFailure.ShouldBeTrue();

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(registerRequest.Email);
        user.DeletionScheduledAt.ShouldBeNull();
        user.LockoutEnd.ShouldBeNull();

        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockout);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockoutNotNeeded);
    }

    [Fact]
    public async Task Erasure_ShouldNotLockTheAccountAgain_WhenAnotherRunErasedItMeanwhile()
    {
        // Two runs can overlap, for example while a deploy starts a second instance. The run that loses
        // on the concurrency stamp must not touch the row the other run erased, nor say it locked it.

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));

        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        ApplicationUser readBeforeTheOtherRun = await db.Users.SingleAsync(row => row.Id == identityId.Value);

        int otherRunCount = await RunFinalizerAsync();
        ApplicationUser erasedByTheOtherRun = await GetUserAsync(identityId.Value);

        using CapturingLoggerFactory loggerFactory = new();
        IAccountErasureService erasureService = CreateErasureService(scope.ServiceProvider, loggerFactory);

        // Act
        Result erasureResult = await erasureService.EraseAsync(readBeforeTheOtherRun, CancellationToken.None);

        // Assert
        otherRunCount.ShouldBe(1);
        erasureResult.IsFailure.ShouldBeTrue();

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(erasedByTheOtherRun.Email);
        user.ConcurrencyStamp.ShouldBe(erasedByTheOtherRun.ConcurrencyStamp);

        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockout);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockoutNotNeeded);
    }

    [Fact]
    public async Task Finalizer_ShouldEraseTheOtherDueAccounts_WhenReadingOneAccountFails()
    {
        // The finalizer reads each account on its own, so one row it cannot read must not stop the
        // accounts after it. The unreadable one has waited longest, so the finalizer comes to it first.

        // Arrange
        (RegisterRequest unreadableRequest, IdentityId unreadableId) = await RegisterAndScheduleDeletionAsync();
        (_, IdentityId otherId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(unreadableId.Value, TimeSpan.FromDays(20));
        await BackdateScheduleAsync(otherId.Value, TimeSpan.FromDays(15));
        Factory.DbCommandFailures.FailNext(
            command => command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                       && CarriesAccountId(command, unreadableId.Value),
            () => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.DataCorrupted));

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        ApplicationUser other = await GetUserAsync(otherId.Value);
        other.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);

        ApplicationUser unreadable = await GetUserAsync(unreadableId.Value);
        unreadable.Email.ShouldBe(unreadableRequest.Email);
        unreadable.DeletionScheduledAt.ShouldNotBeNull();
    }

    private async Task<int> RunFinalizerAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IAccountDeletionFinalizer finalizer =
            scope.ServiceProvider.GetRequiredService<IAccountDeletionFinalizer>();
        return await finalizer.FinalizeDueAccountsAsync(CancellationToken.None);
    }

    private async Task<int> RunFinalizerAsync(ILoggerFactory loggerFactory)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IAccountErasureService erasureService = CreateErasureService(scope.ServiceProvider, loggerFactory);
        IAccountDeletionFinalizer finalizer =
            ActivatorUtilities.CreateInstance<AccountDeletionFinalizer>(scope.ServiceProvider, erasureService);
        return await finalizer.FinalizeDueAccountsAsync(CancellationToken.None);
    }

    private async Task<(int FinalizedCount, IReadOnlyList<Guid> HandedOver)> RunFinalizerAsync(
        Func<Task> beforeFirstErasure)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        ObservedErasure erasureService = new(
            scope.ServiceProvider.GetRequiredService<IAccountErasureService>(), beforeFirstErasure);
        IAccountDeletionFinalizer finalizer =
            ActivatorUtilities.CreateInstance<AccountDeletionFinalizer>(scope.ServiceProvider, erasureService);
        int finalizedCount = await finalizer.FinalizeDueAccountsAsync(CancellationToken.None);
        return (finalizedCount, erasureService.HandedOver);
    }

    /// <summary>
    /// The erasure service with everything from the scope except its logger, so a test can check that
    /// the log about the emergency lock matches what happened to the row (#937).
    /// </summary>
    private static AccountErasureService CreateErasureService(IServiceProvider scopedServices, ILoggerFactory loggerFactory) =>
        ActivatorUtilities.CreateInstance<AccountErasureService>(
            scopedServices, loggerFactory.CreateLogger<AccountErasureService>());

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

    /// <summary>
    /// Arms an undo at a chosen moment. The moment is a parameter because #685 made it decide whether
    /// the row is due at all: an undo armed a second ago holds the erasure back for its whole window.
    /// </summary>
    private async Task ArmRevertTargetAsync(Guid userId, string previousEmail, TimeSpan armedAge)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        ApplicationUser user = await db.Users.SingleAsync(row => row.Id == userId);
        user.EmailChangeRevertTo = previousEmail;
        user.NormalizedEmailChangeRevertTo = previousEmail.ToUpperInvariant();
        user.EmailChangeRevertArmedAt = DateTimeOffset.UtcNow - armedAge;
        await db.SaveChangesAsync();
    }

    private void FailTheNextSaveOf(Guid userId, ErasureSaveFailure failure) =>
        Factory.DbCommandFailures.FailNext(
            command => IsUpdateOfAccount(command, userId),
            () => failure switch
            {
                ErasureSaveFailure.DatabaseError => new PostgresException(
                    "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.NotNullViolation),
                ErasureSaveFailure.LostToAnotherWrite => new DbUpdateConcurrencyException("simulated lost write"),
                _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null)
            });

    /// <summary>
    /// Counts the saves of one account and fails the second one. Other accounts are left alone, so a
    /// save of some other account cannot move the count.
    /// </summary>
    private void FailASecondSaveOf(Guid userId)
    {
        int saves = 0;
        Factory.DbCommandFailures.FailNext(
            command => IsUpdateOfAccount(command, userId) && ++saves > 1,
            () => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.NotNullViolation));
    }

    private static bool IsUpdateOfAccount(DbCommand command, Guid userId) =>
        command.CommandText.Contains($"UPDATE {DatabaseSchemas.Auth}.\"Users\"", StringComparison.Ordinal)
        && CarriesAccountId(command, userId);

    private static bool CarriesAccountId(DbCommand command, Guid userId) =>
        command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == userId);

    private async Task<bool> HasRolesAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.UserRoles.AnyAsync(userRole => userRole.UserId == userId);
    }

    private async Task<ApplicationUser> GetUserAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId);
    }

    /// <summary>
    /// Wraps the real erasure and records every account the finalizer hands to it. It runs a step when
    /// the first account arrives, before the real erasure starts. By then the run has listed every due
    /// account and has read only the first one again.
    /// </summary>
    private sealed class ObservedErasure : IAccountErasureService
    {
        private readonly IAccountErasureService _inner;
        private readonly List<Guid> _handedOver = [];
        private Func<Task>? _step;

        public ObservedErasure(IAccountErasureService inner, Func<Task> step)
        {
            _inner = inner;
            _step = step;
        }

        public IReadOnlyList<Guid> HandedOver => _handedOver;

        public async Task<Result> EraseAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            _handedOver.Add(user.Id);

            Func<Task>? step = _step;
            _step = null;

            if (step is not null)
            {
                await step();
            }

            return await _inner.EraseAsync(user, cancellationToken);
        }
    }

    /// <summary>
    /// A transient error is not on the list: the context retries it, so the erasure never sees it.
    /// </summary>
    public enum ErasureSaveFailure
    {
        /// <summary>An error the retry does not replay. The save throws.</summary>
        DatabaseError,

        /// <summary>Identity turns this one into a failed result instead of an exception.</summary>
        LostToAnotherWrite
    }
}
