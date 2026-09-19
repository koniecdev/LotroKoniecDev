using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Gdpr;

/// <summary>
/// The rule that decides when an account is erased for good (#685). It is written twice — once as the
/// date every promise quotes, once as the query the finalizer runs — so the last test here checks the
/// two readings against each other rather than trusting that they were kept in step by hand.
/// </summary>
public sealed class AccountDeletionScheduleTests
{
    private static readonly DateTimeOffset ScheduledAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromDays(14);
    private static readonly TimeSpan RevertLifespan = TimeSpan.FromDays(14);

    [Fact]
    public void CancellableUntil_IsTheGracePeriodFromTheSchedule_AndIgnoresAnArmedUndo()
    {
        // The cancel token's own lifespan is the grace period, so this date is what says whether the
        // link in the e-mail still works. An armed undo must not move it.
        IAccountDeletionSchedule sut = CreateSut();

        sut.CancellableUntil(ScheduledAt).ShouldBe(ScheduledAt + Grace);
    }

    [Fact]
    public void FinalizesAt_IsTheGracePeriod_WhenNoUndoIsArmed()
    {
        // Also the pre-#684 row: it carries a target but no timestamp, so it reads as nothing armed,
        // which is the behavior it shipped under. There is no honest value to backfill it with.
        IAccountDeletionSchedule sut = CreateSut();

        sut.FinalizesAt(ScheduledAt, revertArmedAt: null).ShouldBe(ScheduledAt + Grace);
    }

    [Fact]
    public void FinalizesAt_IsHeldUntilTheUndoExpires_WhenTheUndoOutlivesTheGracePeriod()
    {
        // The case the ticket is about, made reachable: a grace period shorter than the undo window.
        // With the shipped defaults the two are equal and an undo is always armed before a deletion is
        // scheduled, so this hold never fires — it exists so a shortened Gdpr:DeletionGracePeriod
        // cannot let the finalizer erase an account somebody still holds a working undo link for.
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
        // The comparison this exercises is the one that decides whether data survives, and the grid
        // below cannot reach it: it is day-granular, and every cell where the armed clause sits on its
        // boundary is already answered by the grace clause. Grace is deliberately short here so the
        // armed term is the only thing left deciding, which is what makes a <= / < slip fail.
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
        // The acceptance criterion of #685, stated as a test: no path erases an account while a live
        // undo link for it exists, because following that link cancels the deletion.
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
        // The whole point of one service owning both forms. The query is what erases data and the
        // scalar is what every promise quotes; a disagreement between them is either an account
        // erased early or a deletion date nobody keeps. Grace is short here so both terms get to
        // decide some of the cells - at the shipped 14/14 the grace term would answer them all.
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
