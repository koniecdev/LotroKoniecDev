using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.Tests.Unit.Tests.Models;

public sealed class DatFileLocationTests
{
    [Theory]
    [InlineData(DatFileSource.StandingStoneGames, false)]
    [InlineData(DatFileSource.Steam, false)]
    [InlineData(DatFileSource.Registry, false)]
    [InlineData(DatFileSource.DiskScan, true)]
    [InlineData(DatFileSource.LocalFallback, true)]
    [InlineData((DatFileSource)99, true)]
    public void NeedsConfirmation_BySource_AsksForEverythingButAKnownInstall(DatFileSource source, bool expected)
    {
        // Arrange: the last row stands for a source added later, which must ask until someone
        // decides otherwise (#1292).
        DatFileLocation location = new(Path.Combine("Games", "LOTRO", "client_local_English.dat"), source, "any");

        // Act
        bool needsConfirmation = location.NeedsConfirmation;

        // Assert
        needsConfirmation.ShouldBe(expected);
    }

    [Fact]
    public void NeedsConfirmation_ForEveryKnownSource_HasARowAbove()
    {
        // A new enum value must get its own row in the theory above, so the allow-list is a choice.
        Enum.GetValues<DatFileSource>().Length.ShouldBe(5);
    }
}
