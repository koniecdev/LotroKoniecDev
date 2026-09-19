using System.Linq.Expressions;
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

    [Fact]
    public void FinalizesAt_IsTheGracePeriod_WhenTheRowWasArmedBeforeTheTimestampExisted()
    {
        // A row armed before #684 carries a target but no timestamp. It reads as nothing armed, which
        // is the behavior it shipped under; there is no honest value to backfill it with.
        IAccountDeletionSchedule sut = CreateSut();

        sut.FinalizesAt(ScheduledAt, revertArmedAt: null).ShouldBe(ScheduledAt + Grace);
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

    [Fact]
    public void IsDueBy_AndFinalizesAt_AgreeOnEveryCombinationOfTheTwoClocks()
    {
        // The whole point of one service owning both forms. The query is what erases data and the
        // scalar is what every promise quotes; a disagreement between them is either an account
        // erased early or a deletion date nobody keeps.
        IAccountDeletionSchedule sut = CreateSut(grace: TimeSpan.FromDays(7));
        DateTimeOffset moment = ScheduledAt + TimeSpan.FromDays(10);
        Expression<Func<ApplicationUser, bool>> predicate = sut.IsDueBy(moment);
        Func<ApplicationUser, bool> isDue = predicate.Compile();

        foreach (int scheduledDaysAgo in new[] { 0, 3, 7, 8, 11, 30 })
        {
            foreach (int? armedDaysBeforeSchedule in new int?[] { null, 0, 1, 5, 14, 20 })
            {
                DateTimeOffset scheduledAt = moment - TimeSpan.FromDays(scheduledDaysAgo);
                DateTimeOffset? armedAt = armedDaysBeforeSchedule is { } days
                    ? scheduledAt - TimeSpan.FromDays(days)
                    : null;

                ApplicationUser user = CreateUser(scheduledAt, armedAt);

                isDue(user).ShouldBe(
                    sut.FinalizesAt(scheduledAt, armedAt) <= moment,
                    $"scheduled {scheduledDaysAgo}d ago, armed {armedDaysBeforeSchedule?.ToString() ?? "never"}d before that");
            }
        }
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
