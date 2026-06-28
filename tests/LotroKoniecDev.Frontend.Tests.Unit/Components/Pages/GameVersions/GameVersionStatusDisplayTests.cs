using LotroKoniecDev.Frontend.Components.Pages.GameVersions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate.Enums;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.GameVersions;

public sealed class GameVersionStatusDisplayTests
{
    [Theory]
    [InlineData(GameVersionStatus.Unprocessed, "Nieprzetworzona")]
    [InlineData(GameVersionStatus.Processed, "Przetworzona")]
    [InlineData(GameVersionStatus.Superseded, "Zastąpiona")]
    [InlineData(GameVersionStatus.Unset, "Nieznany")]
    public void Label_ReturnsThePolishLabelForEveryStatus(GameVersionStatus status, string expected)
    {
        GameVersionStatusDisplay.Label(status).ShouldBe(expected);
    }

    [Theory]
    [InlineData(GameVersionStatus.Processed, "badge-success")]
    [InlineData(GameVersionStatus.Unprocessed, "badge-warning")]
    [InlineData(GameVersionStatus.Superseded, "badge-neutral")]
    [InlineData(GameVersionStatus.Unset, "badge-neutral")]
    public void BadgeClass_MapsEveryStatusToAKnownBadgeClass(GameVersionStatus status, string expected)
    {
        GameVersionStatusDisplay.BadgeClass(status).ShouldBe(expected);
    }
}
