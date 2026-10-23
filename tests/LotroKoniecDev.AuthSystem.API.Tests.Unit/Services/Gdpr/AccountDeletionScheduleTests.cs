using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Gdpr;

/// <summary>
/// The rule that decides when an account is erased for good (#685). It is written twice, as the date
/// every promise quotes and as the query the finalizer runs, so the last tests check the two readings
/// against each other.
/// </summary>
public sealed class AccountDeletionScheduleTests
{
    private static readonly DateTimeOffset ScheduledAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(14);
    private static readonly TimeSpan RevertLifespan = TimeSpan.FromDays(14);

    [Fact]
    public void CancellableUntil_IsTheGracePeriodFromTheSchedule_AndIgnoresAnArmedUndo()
    {
        // The cancel endpoint refuses the link from this date on, whatever the token's own lifespan
        // (#947). An armed undo must not move it.
        IAccountDeletionSchedule sut = CreateSut();

        sut.CancellableUntil(ScheduledAt).ShouldBe(ScheduledAt + Grace);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void IsCancellableAt_OneSecondAroundCancellableUntil_IsFalseFromThatMomentOn(
        int secondsPastTheWindow, bool expectedCancellable)
    {
        // The link is dead at the exact moment the finalizer's grace term is met, never one tick after
        // it (#947). One second either side of it is where an off-by-one would hide.
        IAccountDeletionSchedule sut = CreateSut();
        DateTimeOffset moment = sut.CancellableUntil(ScheduledAt) + TimeSpan.FromSeconds(secondsPastTheWindow);

        bool cancellable = sut.IsCancellableAt(ScheduledAt, moment);

        cancellable.ShouldBe(expectedCancellable);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void IsCancellableAt_EndsExactlyWhenIsDueByPicksUpTheAccount_WhenNoUndoIsArmed(int secondsPastTheWindow)
    {
        // With no undo armed the cancel window and the erasure share one boundary, so there is no
        // moment at which a working link points at an account the finalizer may already erase.
        IAccountDeletionSchedule sut = CreateSut();
        ApplicationUser user = CreateUser(ScheduledAt, armedAt: null);
        DateTimeOffset moment = sut.CancellableUntil(ScheduledAt) + TimeSpan.FromSeconds(secondsPastTheWindow);

        bool cancellable = sut.IsCancellableAt(ScheduledAt, moment);

        cancellable.ShouldBe(!sut.IsDueBy(moment).Compile()(user));
    }

    [Fact]
    public void FinalizesAt_IsTheGracePeriod_WhenNoUndoIsArmed()
    {
        // This also covers a row from before #684: a target with no timestamp reads as nothing armed.
        // There is no honest value to backfill it with.
        IAccountDeletionSchedule sut = CreateSut();

        sut.FinalizesAt(ScheduledAt, revertArmedAt: null).ShouldBe(ScheduledAt + Grace);
    }

    [Fact]
    public void FinalizesAt_IsHeldUntilTheUndoExpires_WhenTheUndoOutlivesTheGracePeriod()
    {
        // The case #685 is about: a grace period shorter than the undo window, which the shipped defaults
        // never have. The hold keeps a shortened Gdpr:DeletionGracePeriod from erasing an account while an
        // undo link still works.
        IAccountDeletionSchedule sut = CreateSut(grace: TimeSpan.FromDays(7));
        DateTimeOffset armedAt = ScheduledAt - TimeSpan.FromDays(2);

        sut.FinalizesAt(ScheduledAt, armedAt).ShouldBe(armedAt + RevertLifespan);
    }

    [Fact]
    public void FinalizesAt_IsTheGracePeriod_WhenTheUndoExpiresFirst()
    {
        IAccountDeletionSchedule sut = CreateSut();
        DateTimeOffset armedAt = ScheduledAt - TimeSpan.FromDays(3);

        sut.FinalizesAt(ScheduledAt, armedAt).ShouldBe(ScheduledAt + Grace);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void IsDueBy_AgreesWithFinalizesAt_AtTheBoundaryItself(int offsetSeconds)
    {
        // The exact moment the promise names must also be the moment the query starts returning the
        // row. One second either side of it is where an off-by-one would hide.
        IAccountDeletionSchedule sut = CreateSut();
        ApplicationUser user = CreateUser(ScheduledAt, armedAt: null);
        DateTimeOffset finalizesAt = sut.FinalizesAt(ScheduledAt, revertArmedAt: null);
        DateTimeOffset moment = finalizesAt + TimeSpan.FromSeconds(offsetSeconds);

        bool due = sut.IsDueBy(moment).Compile()(user);

        due.ShouldBe(finalizesAt <= moment);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IsDueBy_HoldsTheRowUntilTheUndoItselfHasExpired_ToTheSecond(
        int secondsPastTheUndoExpiry, bool expectedDue)
    {
        // The day-granular grid below cannot reach this boundary, because the grace term answers every cell
        // there. A short grace leaves the armed term deciding, so a <= / < slip fails.
        IAccountDeletionSchedule sut = CreateSut(grace: TimeSpan.FromDays(7));
        DateTimeOffset armedAt = ScheduledAt - TimeSpan.FromDays(2);
        ApplicationUser user = CreateUser(ScheduledAt, armedAt);
        DateTimeOffset undoExpiresAt = armedAt + RevertLifespan;
        DateTimeOffset moment = undoExpiresAt + TimeSpan.FromSeconds(secondsPastTheUndoExpiry);

        // The grace term is long past, so it cannot mask the armed one.
        moment.ShouldBeGreaterThan(ScheduledAt + TimeSpan.FromDays(7));

        sut.IsDueBy(moment).Compile()(user).ShouldBe(expectedDue);
    }

    [Fact]
    public void IsDueBy_NeverPicksUpAnAccount_WhoseUndoIsStillLive()
    {
        // #685: no path erases an account while a live undo link for it exists, because following that
        // link cancels the deletion.
        IAccountDeletionSchedule sut = CreateSut(grace: TimeSpan.FromDays(7));
        DateTimeOffset armedAt = ScheduledAt - TimeSpan.FromDays(2);
        ApplicationUser user = CreateUser(ScheduledAt, armedAt);
        DateTimeOffset whileUndoIsLive = ScheduledAt + TimeSpan.FromDays(8);

        bool due = sut.IsDueBy(whileUndoIsLive).Compile()(user);

        due.ShouldBeFalse();
        sut.FinalizesAt(ScheduledAt, armedAt).ShouldBeGreaterThan(whileUndoIsLive);
    }

    public static TheoryData<int, int?> ClockCombinations()
    {
        TheoryData<int, int?> data = new();

        foreach (int scheduledDaysAgo in new[] { 0, 3, 7, 8, 11, 30 })
        {
            foreach (int? armedDaysBeforeSchedule in new int?[] { null, 0, 1, 5, 13, 14, 15, 20 })
            {
                data.Add(scheduledDaysAgo, armedDaysBeforeSchedule);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ClockCombinations))]
    public void IsDueBy_AndFinalizesAt_AgreeOnEveryCombinationOfTheTwoClocks(
        int scheduledDaysAgo, int? armedDaysBeforeSchedule)
    {
        // The query erases data and the scalar is what every promise quotes, so a disagreement erases early
        // or breaks a promised date. A short grace lets both terms decide some cells.
        IAccountDeletionSchedule sut = CreateSut(grace: TimeSpan.FromDays(7));
        DateTimeOffset moment = ScheduledAt + TimeSpan.FromDays(10);
        DateTimeOffset scheduledAt = moment - TimeSpan.FromDays(scheduledDaysAgo);
        DateTimeOffset? armedAt = armedDaysBeforeSchedule is { } days
            ? scheduledAt - TimeSpan.FromDays(days)
            : null;
        ApplicationUser user = CreateUser(scheduledAt, armedAt);

        bool due = sut.IsDueBy(moment).Compile()(user);

        due.ShouldBe(sut.FinalizesAt(scheduledAt, armedAt) <= moment);
    }

    [Theory]
    [MemberData(nameof(ClockCombinations))]
    public void IsDueAt_AgreesWithIsDueBy_OnEveryCombinationOfTheTwoClocks(
        int scheduledDaysAgo, int? armedDaysBeforeSchedule)
    {
        // The login page reads IsDueAt and the finalizer runs IsDueBy (#916). If they disagreed, the page
        // would call a deletion under way that the next run does not pick up, or the reverse.
        IAccountDeletionSchedule sut = CreateSut(grace: TimeSpan.FromDays(7));
        DateTimeOffset moment = ScheduledAt + TimeSpan.FromDays(10);
        DateTimeOffset scheduledAt = moment - TimeSpan.FromDays(scheduledDaysAgo);
        DateTimeOffset? armedAt = armedDaysBeforeSchedule is { } days
            ? scheduledAt - TimeSpan.FromDays(days)
            : null;
        ApplicationUser user = CreateUser(scheduledAt, armedAt);

        bool due = sut.IsDueAt(scheduledAt, armedAt, moment);

        due.ShouldBe(sut.IsDueBy(moment).Compile()(user));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IsDueAt_OneSecondAroundAnUndoThatHoldsTheDate_IsTrueFromThatMomentOn(
        int secondsPastTheUndoExpiry, bool expectedDue)
    {
        // The grid above is day-granular, so the second-level boundary of the armed term is pinned here,
        // with the same short grace as the IsDueBy test that does this for the query.
        IAccountDeletionSchedule sut = CreateSut(grace: TimeSpan.FromDays(7));
        DateTimeOffset armedAt = ScheduledAt - TimeSpan.FromDays(2);
        DateTimeOffset moment = armedAt + RevertLifespan + TimeSpan.FromSeconds(secondsPastTheUndoExpiry);

        bool due = sut.IsDueAt(ScheduledAt, armedAt, moment);

        due.ShouldBe(expectedDue);
    }

    [Fact]
    public void IsDueBy_IgnoresAnAccount_WithNoScheduledDeletion()
    {
        IAccountDeletionSchedule sut = CreateSut();
        ApplicationUser user = new() { Id = Guid.NewGuid(), Email = "frodo@shire.me" };

        bool due = sut.IsDueBy(ScheduledAt + TimeSpan.FromDays(365)).Compile()(user);

        due.ShouldBeFalse();
    }

    private static ApplicationUser CreateUser(DateTimeOffset scheduledAt, DateTimeOffset? armedAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            Email = "frodo@shire.me",
            DeletionScheduledAt = scheduledAt,
            EmailChangeRevertArmedAt = armedAt
        };

    private static IAccountDeletionSchedule CreateSut(TimeSpan? grace = null)
    {
        EmailChangeRevertWindow revertWindow = new(
            Microsoft.Extensions.Options.Options.Create(
                new EmailChangeRevertTokenProviderOptions { TokenLifespan = RevertLifespan }));

        return new AccountDeletionSchedule(
            revertWindow,
            Microsoft.Extensions.Options.Options.Create(
                new GdprSettings { DeletionGracePeriod = grace ?? Grace }));
    }
}
