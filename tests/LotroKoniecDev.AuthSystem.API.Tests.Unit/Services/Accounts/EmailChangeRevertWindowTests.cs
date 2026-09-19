using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Accounts;

/// <summary>
/// The one clock behind the undo of ADR-0048. The address reservation of #684 and the erasure hold of
/// #685 both read it, so "live" has to mean the same thing to a boolean check and to the cutoff a
/// query compares a column against.
/// </summary>
public sealed class EmailChangeRevertWindowTests
{
    private static readonly DateTimeOffset ArmedAt = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lifespan = TimeSpan.FromDays(14);

    [Fact]
    public void ExpiresAt_IsTheLifespanFromTheArmingMoment()
    {
        IEmailChangeRevertWindow sut = CreateSut();

        sut.ExpiresAt(ArmedAt).ShouldBe(ArmedAt + Lifespan);
    }

    [Fact]
    public void ExpiresAt_IsNull_WhenNothingIsArmed()
    {
        // A row armed before #684 carries a target but no timestamp, and reads as nothing armed.
        IEmailChangeRevertWindow sut = CreateSut();

        sut.ExpiresAt(null).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(13, true)]
    [InlineData(14, false)]
    [InlineData(15, false)]
    public void IsLiveAt_AnswersByTheLifespan(int daysAfterArming, bool expectedLive)
    {
        // The boundary is exclusive: at exactly the lifespan the undo is dead. The reservation query
        // has always read it that way, so the hold reads it that way too.
        IEmailChangeRevertWindow sut = CreateSut();

        sut.IsLiveAt(ArmedAt, ArmedAt + TimeSpan.FromDays(daysAfterArming)).ShouldBe(expectedLive);
    }

    [Fact]
    public void IsLiveAt_IsFalse_WhenNothingIsArmed()
    {
        IEmailChangeRevertWindow sut = CreateSut();

        sut.IsLiveAt(null, ArmedAt).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(21)]
    public void LiveSince_IsTheCutoffThatMakesTheQueryAgreeWithIsLiveAt(int daysAfterArming)
    {
        // A query cannot call IsLiveAt, so it compares the column with this cutoff instead. The two
        // have to reach the same verdict or an address is freed while its link still works.
        IEmailChangeRevertWindow sut = CreateSut();
        DateTimeOffset moment = ArmedAt + TimeSpan.FromDays(daysAfterArming);

        bool liveByCutoff = ArmedAt > sut.LiveSince(moment);

        liveByCutoff.ShouldBe(sut.IsLiveAt(ArmedAt, moment));
    }

    private static IEmailChangeRevertWindow CreateSut() =>
        new EmailChangeRevertWindow(
            Microsoft.Extensions.Options.Options.Create(
                new EmailChangeRevertTokenProviderOptions { TokenLifespan = Lifespan }));
}
