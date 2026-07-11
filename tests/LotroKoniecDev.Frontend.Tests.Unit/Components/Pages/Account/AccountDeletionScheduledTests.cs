using LotroKoniecDev.Frontend.Components.Pages.Account;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Account;

/// <summary>
/// The anonymous "deletion scheduled" confirmation page: the <c>until</c> query value is
/// user-tamperable, so the date line must parse it defensively — a valid ISO instant renders in
/// Polish time, anything else falls back to the generic 14-day phrasing.
/// </summary>
public sealed class AccountDeletionScheduledTests : BunitContext
{
    [Fact]
    public void BuildDeletionDateLine_WithAValidIsoInstant_RendersItInPolishTime()
    {
        // 2026-07-25 10:00 UTC is 12:00 in Europe/Warsaw (CEST, UTC+2).
        string line = AccountDeletionScheduled.BuildDeletionDateLine("2026-07-25T10:00:00.0000000+00:00");

        line.ShouldBe("Konto zostanie trwale usunięte 2026-07-25 12:00 (czasu polskiego).");
    }

    [Fact]
    public void BuildDeletionDateLine_WithAWinterInstant_UsesTheStandardOffset()
    {
        // 2026-12-10 10:00 UTC is 11:00 in Europe/Warsaw (CET, UTC+1).
        string line = AccountDeletionScheduled.BuildDeletionDateLine("2026-12-10T10:00:00.0000000+00:00");

        line.ShouldBe("Konto zostanie trwale usunięte 2026-12-10 11:00 (czasu polskiego).");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-13-45T99:00:00Z")]
    [InlineData("<script>alert(1)</script>")]
    public void BuildDeletionDateLine_WithATamperedValue_FallsBackToTheGenericPhrasing(string? until)
    {
        string line = AccountDeletionScheduled.BuildDeletionDateLine(until);

        line.ShouldBe("Konto zostanie trwale usunięte po upływie 14 dni od zgłoszenia.");
    }

    [Fact]
    public void Render_WithoutAQueryValue_ShowsTheFallbackLineAndTheHomeLink()
    {
        IRenderedComponent<AccountDeletionScheduled> component = Render<AccountDeletionScheduled>();

        component.Find("[data-testid=deletion-date-line]").TextContent
            .ShouldBe("Konto zostanie trwale usunięte po upływie 14 dni od zgłoszenia.");
        component.Find("a[href='/']").ShouldNotBeNull();
    }
}
