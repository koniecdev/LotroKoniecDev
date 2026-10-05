using System.Data.Common;
using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenIddict.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Bases;
using LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared.Factories;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Register;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;
using LotroKoniecDev.AuthSystem.Persistence.Sessions;
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
        FailASecondSaveOf(identityId.Value, ErasureSaveFailure.DatabaseError);

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
    [InlineData(ErasureSaveFailure.DatabaseError, EventIds.GdprErasureAuthFailed)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite, EventIds.GdprErasureAnonymizationFailed)]
    public async Task Finalizer_ShouldRaiseTheAlertAndPromiseARetry_WhenTheErasureSaveFailed(
        ErasureSaveFailure failure,
        int criticalEventId)
    {
        // The account still waits, so the retry comes (the test above). These lines are the alert for
        // a failed erasure, and they may say so only because it is true (#962).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        FailTheNextSaveOf(identityId.Value, failure);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        loggerFactory.Entries.ShouldContain(entry =>
            entry.EventId.Id == criticalEventId && entry.Level == LogLevel.Critical);
        loggerFactory.Entries.ShouldContain(entry =>
            entry.EventId.Id == EventIds.GdprDeletionFinalizerUserFailed
            && entry.Message.Contains("Will retry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Finalizer_ShouldRaiseTheAlertAndRetry_WhenTheEmergencyLockFailsToo()
    {
        // Another write changed the account while the erasure ran, so the save loses on the concurrency
        // stamp while the account still waits. Then the lock fails as well. The read after it finds the
        // account still waiting, so the run raises the lock's alert once and promises the retry, and here
        // the retry really comes (#962, #980).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int failedRunCount = await RunFinalizerAsync(loggerFactory, beforeFirstErasure: async () =>
        {
            await ChangeTheConcurrencyStampAsync(identityId.Value);
            FailTheEmergencyLockOf(identityId.Value);
        });
        int retryRunCount = await RunFinalizerAsync();

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        failedRunCount.ShouldBe(0);
        retryRunCount.ShouldBe(1);

        loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockoutFailed)
            .ShouldHaveSingleItem()
            .Level.ShouldBe(LogLevel.Critical);
        loggerFactory.Entries.ShouldContain(entry =>
            entry.EventId.Id == EventIds.GdprErasureAnonymizationFailed && entry.Level == LogLevel.Critical);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprDeletionFinalizerUserFailed);
        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureUnneededLockoutFailed);
    }

    [Fact]
    public async Task Finalizer_ShouldRaiseTheLockAlertOnceAndRetry_WhenADatabaseErrorMeetsAFailedLock()
    {
        // The save fails on a real database error while the account still waits, and the lock fails too.
        // The lock's alert is written once, and the retry comes (#980).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        FailTheNextSaveOf(identityId.Value, ErasureSaveFailure.DatabaseError);
        FailTheEmergencyLockOf(identityId.Value);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int failedRunCount = await RunFinalizerAsync(loggerFactory);
        int retryRunCount = await RunFinalizerAsync();

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(2);
        failedRunCount.ShouldBe(0);
        retryRunCount.ShouldBe(1);

        loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockoutFailed)
            .ShouldHaveSingleItem()
            .Level.ShouldBe(LogLevel.Critical);
        loggerFactory.Entries.ShouldContain(entry =>
            entry.EventId.Id == EventIds.GdprErasureAuthFailed && entry.Level == LogLevel.Critical);
        loggerFactory.Entries.ShouldContain(entry =>
            entry.EventId.Id == EventIds.GdprDeletionFinalizerUserFailed
            && entry.Message.Contains("Will retry", StringComparison.Ordinal));
        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureUnneededLockoutFailed);
    }

    [Theory]
    [InlineData(MidErasureChange.OwnerCancels)]
    [InlineData(MidErasureChange.AnotherRunErasesIt)]
    public async Task Finalizer_ShouldNotSayItWillRetry_WhenTheLockFailsOnAnAccountThatStoppedWaiting(MidErasureChange change)
    {
        // The account stops waiting during its erasure, so the save loses on the concurrency stamp, and
        // then the lock fails. No lock was needed and no run comes back to the account, so nothing may
        // ask a person to look or promise a retry (#980).

        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync(registerRequest.Email);
        string cancelToken = AccountDeletionEmailSpy.LastCancelTokenSentTo(registerRequest.Email)!;
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory, beforeFirstErasure: async () =>
        {
            await (change switch
            {
                MidErasureChange.OwnerCancels => CancelDeletionAsync(registerRequest.Email, cancelToken),
                MidErasureChange.AnotherRunErasesIt => RunFinalizerAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
            });
            FailTheEmergencyLockOf(identityId.Value);
        });

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        finalizedCount.ShouldBe(0);

        loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureUnneededLockoutFailed)
            .ShouldHaveSingleItem()
            .Level.ShouldBe(LogLevel.Warning);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureNoLongerWaiting);
        loggerFactory.Entries.ShouldNotContain(entry => entry.Message.Contains("will retry", StringComparison.OrdinalIgnoreCase));
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Finalizer_ShouldRaiseTheLockAlertAndRetry_WhenTheCheckWhetherTheSaveLandedFailsAfterAFailedLock()
    {
        // Nothing is known about the account, so the run keeps the answer it gave before #980: the retry
        // is assumed, and the lock's alert asks a person to look. Here the account still waits, so the
        // retry comes.

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        FailTheNextSaveOf(identityId.Value, ErasureSaveFailure.DatabaseError);
        FailTheEmergencyLockOf(identityId.Value);
        Factory.DbCommandFailures.FailNext(
            command => IsReadOfAnErasedAccount(command, identityId.Value),
            CreateDataCorruption);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int failedRunCount = await RunFinalizerAsync(loggerFactory);
        int retryRunCount = await RunFinalizerAsync();

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(3);
        failedRunCount.ShouldBe(0);
        retryRunCount.ShouldBe(1);

        loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockoutFailed)
            .ShouldHaveSingleItem()
            .Level.ShouldBe(LogLevel.Critical);
        loggerFactory.Entries.ShouldContain(entry =>
            entry.EventId.Id == EventIds.GdprErasureAuthFailed && entry.Level == LogLevel.Critical);
        loggerFactory.Entries.ShouldContain(entry =>
            entry.EventId.Id == EventIds.GdprDeletionFinalizerUserFailed
            && entry.Message.Contains("Will retry", StringComparison.Ordinal));
        loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureSaveCheckFailedAfterFailedLock)
            .ShouldHaveSingleItem()
            .Level.ShouldBe(LogLevel.Warning);
        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureSaveOutcomeUnknown);
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
        Result<AccountErasureOutcome> erasureResult =
            await erasureService.EraseAsync(readBeforeTheCancel);

        // Assert
        erasureResult.IsSuccess.ShouldBeTrue();
        erasureResult.Value.ShouldBe(AccountErasureOutcome.NoLongerWaiting);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(registerRequest.Email);
        user.DeletionScheduledAt.ShouldBeNull();
        user.LockoutEnd.ShouldBeNull();

        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockout);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureNoLongerWaiting);
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
        Result<AccountErasureOutcome> erasureResult =
            await erasureService.EraseAsync(readBeforeTheOtherRun);

        // Assert
        otherRunCount.ShouldBe(1);
        erasureResult.IsSuccess.ShouldBeTrue();
        erasureResult.Value.ShouldBe(AccountErasureOutcome.NoLongerWaiting);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldBe(erasedByTheOtherRun.Email);
        user.ConcurrencyStamp.ShouldBe(erasedByTheOtherRun.ConcurrencyStamp);

        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureEmergencyLockout);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureNoLongerWaiting);
    }

    [Theory]
    [InlineData(MidErasureChange.OwnerCancels)]
    [InlineData(MidErasureChange.AnotherRunErasesIt)]
    public async Task Finalizer_ShouldNotSayItWillRetry_WhenTheAccountStopsWaitingDuringItsErasure(MidErasureChange change)
    {
        // The run has read the account again and hands it to the erasure, and only then does the account
        // stop waiting. The erasure save loses on the concurrency stamp, but no run will come back to the
        // account, and nothing is wrong that a person has to look at (#962).

        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync(registerRequest.Email);
        string cancelToken = AccountDeletionEmailSpy.LastCancelTokenSentTo(registerRequest.Email)!;
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory, beforeFirstErasure: () => change switch
        {
            MidErasureChange.OwnerCancels => CancelDeletionAsync(registerRequest.Email, cancelToken),
            MidErasureChange.AnotherRunErasesIt => RunFinalizerAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(change), change, null)
        });

        // Assert
        finalizedCount.ShouldBe(0);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureNoLongerWaiting);
        loggerFactory.Entries.ShouldNotContain(entry => entry.Message.Contains("will retry", StringComparison.OrdinalIgnoreCase));
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Finalizer_ShouldWarnWithoutPromisingARetry_WhenADatabaseErrorMeetsACancel()
    {
        // A race alone is logged at Information. Here the save hit a real database error, and the next
        // account may hit it too, so the same line is a warning. The owner cancelled, so it still
        // promises no retry (#962).

        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync(registerRequest.Email);
        string cancelToken = AccountDeletionEmailSpy.LastCancelTokenSentTo(registerRequest.Email)!;
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory, beforeFirstErasure: async () =>
        {
            await CancelDeletionAsync(registerRequest.Email, cancelToken);
            Factory.DbCommandFailures.FailNext(
                command => IsUpdateOfAccount(command, identityId.Value) && CarriesAnAnonymizedAddress(command),
                () => CreateFailure(ErasureSaveFailure.DatabaseError));
        });

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        finalizedCount.ShouldBe(0);

        CapturingLoggerFactory.LogEntry noLongerWaiting = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureNoLongerWaiting)
            .ShouldHaveSingleItem();
        noLongerWaiting.Level.ShouldBe(LogLevel.Warning);
        loggerFactory.Entries.ShouldNotContain(entry => entry.Message.Contains("will retry", StringComparison.OrdinalIgnoreCase));
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Finalizer_ShouldWarnWithoutPromisingARetry_WhenItCannotTellWhetherTheErasureSaveLanded()
    {
        // The owner cancelled, so the lock matches no row, and then the read that tells a landed save
        // from a cancel fails. No run comes back to the account either way. If the save did land, its
        // cleanup never runs, so the line is a warning, not a promise (#962).

        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await AccountDeletionEmailSpy.WaitForScheduledCaptureAsync(registerRequest.Email);
        string cancelToken = AccountDeletionEmailSpy.LastCancelTokenSentTo(registerRequest.Email)!;
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory, beforeFirstErasure: async () =>
        {
            await CancelDeletionAsync(registerRequest.Email, cancelToken);
            Factory.DbCommandFailures.FailNext(
                command => IsReadOfAnErasedAccount(command, identityId.Value),
                CreateDataCorruption);
        });

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        finalizedCount.ShouldBe(0);

        CapturingLoggerFactory.LogEntry unknown = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureSaveOutcomeUnknown)
            .ShouldHaveSingleItem();
        unknown.Level.ShouldBe(LogLevel.Warning);
        loggerFactory.Entries.ShouldNotContain(entry => entry.Message.Contains("will retry", StringComparison.OrdinalIgnoreCase));
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Finalizer_ShouldCleanUpAndCountTheAccount_WhenTheErasureSaveLandedButItsAnswerWasLost()
    {
        // The save lands and its answer is lost. EF runs the save again with the old concurrency stamp,
        // so Identity reports a conflict. The account is erased all the same, and no later run comes back
        // to it, so this run has to finish the cleanup (#962).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        Factory.DbCommandFailures.FailNextAfterItRuns(
            command => IsUpdateOfAccount(command, identityId.Value),
            CreateTransientFailure);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory);

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        finalizedCount.ShouldBe(1);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
        (await HasRolesAsync(identityId.Value)).ShouldBeFalse();

        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureSaveLandedAfterAll);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleaned);
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Fact]
    public async Task Finalizer_ShouldCleanUpAndCountTheAccount_WhenTheErasureSaveLandedButItsAnswerWasLostAndTheLockFailed()
    {
        // The save lands, its answer is lost, and then the emergency lock fails as well. A failed lock says
        // nothing about the account, so the run still checks whether its own save landed. It did, so the
        // lock was not needed, nobody has to look, and no later run comes back for the cleanup (#980).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        Factory.DbCommandFailures.FailNextAfterItRuns(
            command => IsUpdateOfAccount(command, identityId.Value),
            CreateTransientFailure);
        FailTheEmergencyLockOf(identityId.Value);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory);

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(2);
        finalizedCount.ShouldBe(1);

        ApplicationUser user = await GetUserAsync(identityId.Value);
        user.Email.ShouldEndWith(AnonymizationConstants.EmailDomain);
        user.LockoutEnabled.ShouldBeTrue();
        user.LockoutEnd.ShouldBe(DateTimeOffset.MaxValue);
        (await HasRolesAsync(identityId.Value)).ShouldBeFalse();

        loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureUnneededLockoutFailed)
            .ShouldHaveSingleItem()
            .Level.ShouldBe(LogLevel.Warning);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureSaveLandedAfterAll);
        loggerFactory.Entries.ShouldContain(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleaned);
        loggerFactory.Entries.ShouldNotContain(entry => entry.Message.Contains("will retry", StringComparison.OrdinalIgnoreCase));
        loggerFactory.Entries.ShouldNotContain(entry => entry.Level >= LogLevel.Error);
    }

    [Theory]
    [InlineData(ErasureSaveFailure.DatabaseError)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite)]
    public async Task Finalizer_ShouldNameTheRolesStep_WhenRemovingTheRolesFails(ErasureSaveFailure failure)
    {
        // Removing the roles is the next save of the account after the erasure. Identity turns a lost
        // write there into a failed result, which the cleanup used to take for a success (#962).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        FailASecondSaveOf(identityId.Value, failure);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory);

        // Assert
        finalizedCount.ShouldBe(1);
        (await HasRolesAsync(identityId.Value)).ShouldBeTrue();

        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleaned);
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldContain("Failed: roles: ");
    }

    [Fact]
    public async Task Finalizer_ShouldNameTheTokensStep_WhenATokenCannotBeRevoked()
    {
        // OpenIddict reports a failed write on a token as a false result, not as an exception (#962).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await CreateValidTokenAsync(identityId.Value);
        Factory.DbCommandFailures.FailNext(
            command => IsUpdateOf(command, "OpenIddictTokens"),
            () => new DbUpdateConcurrencyException("simulated lost write"));
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(loggerFactory);

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);

        loggerFactory.Entries.ShouldNotContain(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleaned);
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldContain("Failed: tokens: ");
        cleanupFailed.Message.ShouldContain(" not revoked.");
    }

    [Theory]
    [InlineData(ErasureSaveFailure.DatabaseError)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite)]
    public async Task Finalizer_ShouldStillRemoveTheClaims_WhenRemovingTheRolesFails(ErasureSaveFailure failure)
    {
        // A failed step used to stop the cleanup, or to leave its changes in the context for the next
        // save to send again. The claims have nothing wrong of their own, so they go (#981). The roles
        // stay: their delete went with the failed save, and nothing sends it again.

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await AddClaimAsync(identityId.Value);
        FailASecondSaveOf(identityId.Value, failure);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await HasClaimsAsync(identityId.Value)).ShouldBeFalse();
        (await HasRolesAsync(identityId.Value)).ShouldBeTrue();
    }

    [Fact]
    public async Task Finalizer_ShouldStillRemoveTheLogins_WhenTheRolesAndClaimsKeepFailing()
    {
        // The database refuses the deletes of the roles and the claims every time. Neither failed delete
        // goes out again with a later save, so each is sent once and the logins still go (#981).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await AddClaimAsync(identityId.Value);
        await AddLoginAsync(identityId.Value, "Google");
        RefuseTheRolesAndClaimsOf(identityId.Value);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(2);
        (await HasLoginAsync(identityId.Value, "Google")).ShouldBeFalse();
    }

    [Fact]
    public async Task Finalizer_ShouldNameEachFailedStep_WhenTheRolesAndClaimsKeepFailing()
    {
        // Each of the two steps fails for its own reason, so the log names both and carries both
        // exceptions (#981).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await AddClaimAsync(identityId.Value);
        RefuseTheRolesAndClaimsOf(identityId.Value);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldContain("Failed: roles: ");
        cleanupFailed.Message.ShouldContain("; claims: ");
        cleanupFailed.Message.ShouldContain("simulated permanent failure");
        cleanupFailed.Exception.ShouldBeOfType<AggregateException>().InnerExceptions.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Finalizer_ShouldLogTheException_WhenOneCleanupStepThrows()
    {
        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        FailASecondSaveOf(identityId.Value, ErasureSaveFailure.DatabaseError);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Exception.ShouldBeOfType<DbUpdateException>()
            .GetBaseException().ShouldBeOfType<PostgresException>();
    }

    [Fact]
    public async Task Finalizer_ShouldSayTheStepStopped_WhenACleanupStepThrows()
    {
        // A step that throws skips the rest of its own work, and the log has to show that.

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        FailASecondSaveOf(identityId.Value, ErasureSaveFailure.DatabaseError);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldContain("simulated permanent failure (the step stopped here)");
    }

    [Fact]
    public async Task Finalizer_ShouldFinishTheCleanup_WhenTheHostStopsAfterTheRunReadTheAccount()
    {
        // A shutdown must not cut an erasure halfway (#981).

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        string tokenId = await CreateValidTokenAsync(identityId.Value);
        await AddClaimAsync(identityId.Value);
        await AddLoginAsync(identityId.Value, "Google");
        using CancellationTokenSource shutdown = new();

        // Act
        int finalizedCount = await RunFinalizerAsync(NullLoggerFactory.Instance, shutdown.CancelAsync, shutdown.Token);

        // Assert
        shutdown.IsCancellationRequested.ShouldBeTrue();
        finalizedCount.ShouldBe(1);
        (await TokenStatusAsync(tokenId)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
        (await HasRolesAsync(identityId.Value)).ShouldBeFalse();
        (await HasClaimsAsync(identityId.Value)).ShouldBeFalse();
        (await HasLoginAsync(identityId.Value, "Google")).ShouldBeFalse();
    }

    [Fact]
    public async Task Finalizer_ShouldNotStartTheNextAccount_WhenTheHostStops()
    {
        // The erasure takes no cancellation token, so the read of the next account is where a shutdown
        // stops the run (#981).

        // Arrange
        (_, IdentityId firstId) = await RegisterAndScheduleDeletionAsync();
        (RegisterRequest nextRequest, IdentityId nextId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(firstId.Value, TimeSpan.FromDays(20));
        await BackdateScheduleAsync(nextId.Value, TimeSpan.FromDays(15));
        using CancellationTokenSource shutdown = new();

        // Act
        await Should.ThrowAsync<OperationCanceledException>(
            () => RunFinalizerAsync(NullLoggerFactory.Instance, shutdown.CancelAsync, shutdown.Token));

        // Assert
        ApplicationUser next = await GetUserAsync(nextId.Value);
        next.Email.ShouldBe(nextRequest.Email);
    }

    [Fact]
    public async Task Finalizer_ShouldFinishTheCleanup_WhenOneTokenKeepsFailing()
    {
        // OpenIddict keeps a token whose save failed in the context, unless the save lost a concurrency
        // check. Every later save in the cleanup used to send it again and fail with it (#981).

        // Arrange
        (IdentityId identityId, string failingTokenId, string otherTokenId) = await ArrangeATokenThatKeepsFailingAsync();

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await TokenStatusAsync(failingTokenId)).ShouldBe(OpenIddictConstants.Statuses.Valid);
        (await TokenStatusAsync(otherTokenId)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
        (await HasRolesAsync(identityId.Value)).ShouldBeFalse();
    }

    [Fact]
    public async Task Finalizer_ShouldNameOnlyTheFailedToken_WhenOneTokenKeepsFailing()
    {
        // The steps after the failed token used to fail with it, and the log listed those echoes too (#981).

        // Arrange
        (_, string failingTokenId, _) = await ArrangeATokenThatKeepsFailingAsync();
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldContain($"Failed: tokens: {failingTokenId} not revoked. The account");
        cleanupFailed.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Finalizer_ShouldFinishTheCleanup_WhenOneAuthorizationKeepsFailing()
    {
        // OpenIddict keeps an authorization whose save failed in the context, the same way as a token.

        // Arrange
        (IdentityId identityId, string failingAuthorizationId, string otherAuthorizationId) =
            await ArrangeAnAuthorizationThatKeepsFailingAsync();

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await AuthorizationStatusAsync(failingAuthorizationId)).ShouldBe(OpenIddictConstants.Statuses.Valid);
        (await AuthorizationStatusAsync(otherAuthorizationId)).ShouldBe(OpenIddictConstants.Statuses.Revoked);
        (await HasRolesAsync(identityId.Value)).ShouldBeFalse();
    }

    [Fact]
    public async Task Finalizer_ShouldNameOnlyTheFailedAuthorization_WhenOneAuthorizationKeepsFailing()
    {
        // Arrange
        (_, string failingAuthorizationId, _) = await ArrangeAnAuthorizationThatKeepsFailingAsync();
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldContain(
            $"Failed: authorizations: {failingAuthorizationId} not revoked. The account");
        cleanupFailed.Exception.ShouldBeNull();
    }

    [Theory]
    [InlineData(ErasureSaveFailure.DatabaseError)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite)]
    public async Task Finalizer_ShouldRemoveTheOtherLogin_WhenRemovingOneLoginFails(ErasureSaveFailure failure)
    {
        // A lost write leaves the account in the context with a concurrency stamp that was never saved,
        // and a database error ended the whole step. The next login reads the account again and goes
        // on (#981).

        // Arrange
        (IdentityId identityId, _) = await ArrangeTwoLoginsWhoseFirstRemovalFailsAsync(failure);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        (await LoginProvidersOfAsync(identityId.Value)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(ErasureSaveFailure.DatabaseError)]
    [InlineData(ErasureSaveFailure.LostToAnotherWrite)]
    public async Task Finalizer_ShouldNameOnlyTheFailedLogin_WhenRemovingOneLoginFails(ErasureSaveFailure failure)
    {
        // Arrange
        (_, List<string> failedProviders) = await ArrangeTwoLoginsWhoseFirstRemovalFailsAsync(failure);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        string failedProvider = failedProviders.ShouldHaveSingleItem();
        string otherProvider = failedProvider == "Google" ? "Microsoft" : "Google";
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldContain($"Failed: logins: {failedProvider}: ");
        cleanupFailed.Message.ShouldNotContain(otherProvider);
    }

    [Fact]
    public async Task Finalizer_ShouldNotSayTheStepStopped_WhenRemovingOneLoginThrows()
    {
        // Each login catches its own exception, so the logins step goes on after it.

        // Arrange
        await ArrangeTwoLoginsWhoseFirstRemovalFailsAsync(ErasureSaveFailure.DatabaseError);
        using CapturingLoggerFactory loggerFactory = new();

        // Act
        await RunFinalizerAsync(loggerFactory);

        // Assert
        CapturingLoggerFactory.LogEntry cleanupFailed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == EventIds.GdprErasureArtifactsCleanupFailed)
            .ShouldHaveSingleItem();
        cleanupFailed.Message.ShouldNotContain("stopped here");
    }

    [Fact]
    public async Task Finalizer_ShouldNotWriteATokenAgain_WhenItIsAlreadyRevoked()
    {
        // OpenIddict's token manager writes a revoked token again. Scheduling the deletion already revoked
        // the tokens, so each such write is work for nothing, and one that fails is a false warning.

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await CreateTokenAsync(identityId.Value, OpenIddictConstants.Statuses.Revoked);
        Factory.DbCommandFailures.FailEvery(
            command => IsUpdateOf(command, "OpenIddictTokens"),
            () => CreateFailure(ErasureSaveFailure.DatabaseError));

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(0);
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
            CreateDataCorruption);

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

    [Fact]
    public async Task Finalizer_ShouldDeleteTheAccountsSignInSessions_WhenItAnonymizesTheAccount()
    {
        // A stored sign-in session holds the name and e-mail (ADR-0062), so the erasure takes it with the
        // rest. Another account's session is left alone.

        // Arrange
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await AddSignInSessionAsync(identityId.Value);
        await AddSignInSessionAsync(identityId.Value);
        IdentityId otherId =
            await UserFactory.RegisterRandomUserAsync(ApiClient, Faker, AccountConfirmationEmailSpy, TestPassword);
        await AddSignInSessionAsync(otherId.Value);

        // Act
        int finalizedCount = await RunFinalizerAsync();

        // Assert
        finalizedCount.ShouldBe(1);
        (await CountSignInSessionsAsync(identityId.Value)).ShouldBe(0);
        (await CountSignInSessionsAsync(otherId.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task Erasure_ShouldKeepTheOwnersNewSignInSession_WhenTheOwnerCancelledTheDeletionMeanwhile()
    {
        // The erasure read the account, then the owner followed the cancel link and signed in again. Their
        // new session must survive the erasure that lost.

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
        await AddSignInSessionAsync(identityId.Value);

        IAccountErasureService erasureService = scope.ServiceProvider.GetRequiredService<IAccountErasureService>();

        // Act
        Result<AccountErasureOutcome> erasureResult =
            await erasureService.EraseAsync(readBeforeTheCancel);

        // Assert
        erasureResult.IsSuccess.ShouldBeTrue();
        erasureResult.Value.ShouldBe(AccountErasureOutcome.NoLongerWaiting);
        (await CountSignInSessionsAsync(identityId.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task Finalizer_ShouldDeleteTheSignInSessionsOnTheNextRun_WhenTheDeleteFailed()
    {
        // The delete runs before the anonymizing save. Once that save lands the finalizer never comes back
        // to the account, so a delete that failed after it would never run again.

        // Arrange
        (RegisterRequest registerRequest, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await AddSignInSessionAsync(identityId.Value);
        Factory.DbCommandFailures.FailNext(
            command => command.CommandText.Contains(
                $"DELETE FROM {DatabaseSchemas.Auth}.\"SignInSessions\"", StringComparison.Ordinal),
            CreateDataCorruption);

        // Act
        int failedRunCount = await RunFinalizerAsync();
        ApplicationUser afterTheFailedRun = await GetUserAsync(identityId.Value);
        int sessionsAfterTheFailedRun = await CountSignInSessionsAsync(identityId.Value);
        int retriedRunCount = await RunFinalizerAsync();

        // Assert
        Factory.DbCommandFailures.FailuresInjected.ShouldBe(1);
        failedRunCount.ShouldBe(0);
        afterTheFailedRun.Email.ShouldBe(registerRequest.Email);
        sessionsAfterTheFailedRun.ShouldBe(1);
        retriedRunCount.ShouldBe(1);
        (await CountSignInSessionsAsync(identityId.Value)).ShouldBe(0);
    }

    private async Task AddSignInSessionAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        db.SignInSessions.Add(SignInSession.Create(userId, [1, 2, 3], DateTimeOffset.UtcNow.AddDays(30)));
        await db.SaveChangesAsync();
    }

    private async Task<int> CountSignInSessionsAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.SignInSessions.CountAsync(session => session.UserId == userId);
    }

    private async Task<int> RunFinalizerAsync()
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IAccountDeletionFinalizer finalizer =
            scope.ServiceProvider.GetRequiredService<IAccountDeletionFinalizer>();
        return await finalizer.FinalizeDueAccountsAsync(CancellationToken.None);
    }

    /// <summary>
    /// The finalizer and its erasure write to <paramref name="loggerFactory"/>, so a test reads every line
    /// the run wrote about an account.
    /// </summary>
    private async Task<int> RunFinalizerAsync(
        ILoggerFactory loggerFactory,
        Func<Task>? beforeFirstErasure = null,
        CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IAccountErasureService erasureService = CreateErasureService(scope.ServiceProvider, loggerFactory);
        if (beforeFirstErasure is not null)
        {
            erasureService = new ObservedErasure(erasureService, beforeFirstErasure);
        }

        IAccountDeletionFinalizer finalizer = ActivatorUtilities.CreateInstance<AccountDeletionFinalizer>(
            scope.ServiceProvider, erasureService, loggerFactory.CreateLogger<AccountDeletionFinalizer>());
        return await finalizer.FinalizeDueAccountsAsync(cancellationToken);
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
            () => CreateFailure(failure));

    /// <summary>
    /// Counts the saves of one account and fails the second one. Other accounts are left alone, so a
    /// save of some other account cannot move the count.
    /// </summary>
    private void FailASecondSaveOf(Guid userId, ErasureSaveFailure failure)
    {
        int saves = 0;
        Factory.DbCommandFailures.FailNext(
            command => IsUpdateOfAccount(command, userId) && ++saves > 1,
            () => CreateFailure(failure));
    }

    /// <summary>
    /// PostgreSQL hands the tokens back in the order they were made, with no promise to. The asserts hold
    /// in any order, but only this order makes the other token come after the failed one.
    /// </summary>
    private async Task<(IdentityId IdentityId, string FailingId, string OtherId)> ArrangeATokenThatKeepsFailingAsync()
    {
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        string failingId = await CreateValidTokenAsync(identityId.Value);
        string otherId = await CreateValidTokenAsync(identityId.Value);
        Factory.DbCommandFailures.FailEvery(
            command => IsUpdateOf(command, "OpenIddictTokens") && CarriesValue(command, failingId),
            () => CreateFailure(ErasureSaveFailure.DatabaseError));

        return (identityId, failingId, otherId);
    }

    /// <summary>
    /// PostgreSQL most likely hands the authorizations back in the order they were made, like the tokens.
    /// </summary>
    private async Task<(IdentityId IdentityId, string FailingId, string OtherId)> ArrangeAnAuthorizationThatKeepsFailingAsync()
    {
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        string failingId = await CreateValidAuthorizationAsync(identityId.Value);
        string otherId = await CreateValidAuthorizationAsync(identityId.Value);
        Factory.DbCommandFailures.FailEvery(
            command => IsUpdateOf(command, "OpenIddictAuthorizations") && CarriesValue(command, failingId),
            () => CreateFailure(ErasureSaveFailure.DatabaseError));

        return (identityId, failingId, otherId);
    }

    /// <summary>
    /// Fails the first login removal of the account, whichever login PostgreSQL hands back first, so the
    /// other one always comes after it. The removal carries the account's id because EF sends it in one
    /// command with the update of the account. The list gets the provider of the login that failed.
    /// </summary>
    private async Task<(IdentityId IdentityId, List<string> FailedProviders)> ArrangeTwoLoginsWhoseFirstRemovalFailsAsync(
        ErasureSaveFailure failure)
    {
        (_, IdentityId identityId) = await RegisterAndScheduleDeletionAsync();
        await BackdateScheduleAsync(identityId.Value, TimeSpan.FromDays(15));
        await AddLoginAsync(identityId.Value, "Google");
        await AddLoginAsync(identityId.Value, "Microsoft");
        List<string> failedProviders = [];
        Factory.DbCommandFailures.FailNext(
            command =>
            {
                if (!IsDeleteFrom(command, "UserLogins") || !CarriesAccountId(command, identityId.Value))
                {
                    return false;
                }

                failedProviders.AddRange(command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => parameter.Value)
                    .OfType<string>()
                    .Where(value => value is "Google" or "Microsoft"));
                return true;
            },
            () => CreateFailure(failure));

        return (identityId, failedProviders);
    }

    private void RefuseTheRolesAndClaimsOf(Guid userId) =>
        Factory.DbCommandFailures.FailEvery(
            command => (IsDeleteFrom(command, "UserRoles") || IsDeleteFrom(command, "UserClaims"))
                       && CarriesAccountId(command, userId),
            () => CreateFailure(ErasureSaveFailure.DatabaseError));

    private static Exception CreateFailure(ErasureSaveFailure failure) =>
        failure switch
        {
            ErasureSaveFailure.DatabaseError => new PostgresException(
                "simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.NotNullViolation),
            ErasureSaveFailure.LostToAnotherWrite => new DbUpdateConcurrencyException("simulated lost write"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null)
        };

    private void FailTheEmergencyLockOf(Guid userId) =>
        Factory.DbCommandFailures.FailNext(command => IsEmergencyLockOf(command, userId), CreateDataCorruption);

    private static NpgsqlException CreateTransientFailure() =>
        new("The operation has timed out", new TimeoutException());

    /// <summary>
    /// The retry does not replay this error, so the command fails for good.
    /// </summary>
    private static PostgresException CreateDataCorruption() =>
        new("simulated permanent failure", "ERROR", "ERROR", PostgresErrorCodes.DataCorrupted);

    private static bool IsUpdateOfAccount(DbCommand command, Guid userId) =>
        command.CommandText.Contains($"UPDATE {DatabaseSchemas.Auth}.\"Users\"", StringComparison.Ordinal)
        && CarriesAccountId(command, userId);

    /// <summary>
    /// Only the check after a failed erasure save reads the account by its id and an anonymized address.
    /// A background mail job may read the same account by its id at the same moment.
    /// </summary>
    private static bool IsReadOfAnErasedAccount(DbCommand command, Guid userId) =>
        command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
        && CarriesAccountId(command, userId)
        && CarriesAnAnonymizedAddress(command);

    private static bool CarriesAnAnonymizedAddress(DbCommand command) =>
        command.Parameters.Cast<DbParameter>().Any(parameter =>
            parameter.Value is string value
            && value.StartsWith(AnonymizationConstants.EmailPrefix, StringComparison.Ordinal));

    /// <summary>
    /// The emergency lock is the one update of the account that checks for a scheduled deletion.
    /// </summary>
    private static bool IsEmergencyLockOf(DbCommand command, Guid userId) =>
        command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
        && command.CommandText.Contains("\"DeletionScheduledAt\" IS NOT NULL", StringComparison.Ordinal)
        && CarriesAccountId(command, userId);

    private static bool IsUpdateOf(DbCommand command, string table) =>
        command.CommandText.Contains($"UPDATE {DatabaseSchemas.Auth}.\"{table}\"", StringComparison.Ordinal);

    private static bool IsDeleteFrom(DbCommand command, string table) =>
        command.CommandText.Contains($"DELETE FROM {DatabaseSchemas.Auth}.\"{table}\"", StringComparison.Ordinal);

    private static bool CarriesAccountId(DbCommand command, Guid userId) =>
        command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is Guid id && id == userId);

    private static bool CarriesValue(DbCommand command, string value) =>
        command.Parameters.Cast<DbParameter>().Any(parameter => parameter.Value is string text && text == value);

    /// <summary>
    /// A write to the account that is not a cancel: the account still waits, but a save that read it
    /// before now loses on the concurrency stamp.
    /// </summary>
    private async Task ChangeTheConcurrencyStampAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        string concurrencyStamp = Guid.NewGuid().ToString();
        await db.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.ConcurrencyStamp, concurrencyStamp));
    }

    private async Task CancelDeletionAsync(string email, string cancelToken)
    {
        HttpResponseMessage cancelResponse = await ApiClient.Http.PostAsJsonAsync(
            new Uri("auth/account/cancel-deletion", UriKind.Relative),
            new CancelAccountDeletionRequest(email, cancelToken));
        cancelResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// Gives the account a token of its own, so the test does not depend on the tokens that signing in
    /// left behind.
    /// </summary>
    private Task<string> CreateValidTokenAsync(Guid userId) =>
        CreateTokenAsync(userId, OpenIddictConstants.Statuses.Valid);

    private async Task<string> CreateTokenAsync(Guid userId, string status)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictTokenManager tokenManager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        object token = await tokenManager.CreateAsync(new OpenIddictTokenDescriptor
        {
            Subject = userId.ToString(),
            Type = OpenIddictConstants.TokenTypeHints.RefreshToken,
            Status = status,
            CreationDate = DateTimeOffset.UtcNow,
            ExpirationDate = DateTimeOffset.UtcNow.AddDays(30)
        });

        return (await tokenManager.GetIdAsync(token))!;
    }

    private async Task<string> CreateValidAuthorizationAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictAuthorizationManager authorizationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        object authorization = await authorizationManager.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            Subject = userId.ToString(),
            Type = OpenIddictConstants.AuthorizationTypes.Permanent,
            Status = OpenIddictConstants.Statuses.Valid,
            CreationDate = DateTimeOffset.UtcNow
        });

        return (await authorizationManager.GetIdAsync(authorization))!;
    }

    private async Task<string?> AuthorizationStatusAsync(string authorizationId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictAuthorizationManager authorizationManager =
            scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();

        object? authorization = await authorizationManager.FindByIdAsync(authorizationId);
        return authorization is null ? null : await authorizationManager.GetStatusAsync(authorization);
    }

    private async Task<string?> TokenStatusAsync(string tokenId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        IOpenIddictTokenManager tokenManager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();

        object? token = await tokenManager.FindByIdAsync(tokenId);
        return token is null ? null : await tokenManager.GetStatusAsync(token);
    }

    private async Task<bool> HasRolesAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.UserRoles.AnyAsync(userRole => userRole.UserId == userId);
    }

    private async Task AddClaimAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = await userManager.Users.SingleAsync(row => row.Id == userId);
        (await userManager.AddClaimAsync(user, new Claim("test-claim", "test-value"))).Succeeded.ShouldBeTrue();
    }

    private async Task<bool> HasClaimsAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.UserClaims.AnyAsync(userClaim => userClaim.UserId == userId);
    }

    private async Task AddLoginAsync(Guid userId, string provider)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        UserManager<ApplicationUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        ApplicationUser user = await userManager.Users.SingleAsync(row => row.Id == userId);
        UserLoginInfo login = new(provider, $"{provider}-{Guid.NewGuid():N}", provider);
        (await userManager.AddLoginAsync(user, login)).Succeeded.ShouldBeTrue();
    }

    private async Task<List<string>> LoginProvidersOfAsync(Guid userId)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.UserLogins
            .Where(userLogin => userLogin.UserId == userId)
            .Select(userLogin => userLogin.LoginProvider)
            .ToListAsync();
    }

    private async Task<bool> HasLoginAsync(Guid userId, string provider)
    {
        await using AsyncServiceScope scope = Factory.Services.CreateAsyncScope();
        AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.UserLogins.AnyAsync(userLogin => userLogin.UserId == userId && userLogin.LoginProvider == provider);
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

        public async Task<Result<AccountErasureOutcome>> EraseAsync(ApplicationUser user)
        {
            _handedOver.Add(user.Id);

            Func<Task>? step = _step;
            _step = null;

            if (step is not null)
            {
                await step();
            }

            return await _inner.EraseAsync(user);
        }
    }

    public enum MidErasureChange
    {
        OwnerCancels,
        AnotherRunErasesIt
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
