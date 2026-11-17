using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using LotroKoniecDev.DesktopApp.Tests.Unit.Shared;
using LotroKoniecDev.Primitives.Enums;

namespace LotroKoniecDev.DesktopApp.Tests.Unit.Tests;

public sealed class GameFolderCandidateViewModelTests
{
    private static readonly string Folder = ViewModelBuilder.Rooted("Gry", "LOTRO");

    [Theory]
    [InlineData(DatFileSource.StandingStoneGames, "domyślny folder Standing Stone Games", false)]
    [InlineData(DatFileSource.Steam, "Steam", false)]
    [InlineData(DatFileSource.Registry, "wpis w rejestrze Windows", false)]
    [InlineData(DatFileSource.DiskScan, "znaleziony na dysku", true)]
    [InlineData(DatFileSource.LocalFallback, "folder data obok miejsca startu programu", true)]
    [InlineData((DatFileSource)99, "inne źródło", true)]
    public void Constructor_ForEachSource_ShowsTheFolderAPolishSourceNameAndTheGuardsWarning(
        DatFileSource source,
        string expectedLabel,
        bool expectedWarning)
    {
        // Act: the locator's English names never reach the player (ADR-0033 amendment).
        GameFolderCandidateViewModel candidate = new(ViewModelBuilder.Location(Folder, source), _ => { });

        // Assert
        candidate.Folder.ShouldBe(Folder);
        candidate.SourceLabel.ShouldBe(expectedLabel);
        candidate.HasWarning.ShouldBe(expectedWarning);
    }

    [Fact]
    public void ChooseCommand_Always_HandsThisEntryToTheList()
    {
        // Arrange
        GameFolderCandidateViewModel? chosen = null;
        GameFolderCandidateViewModel candidate = new(
            ViewModelBuilder.Location(Folder, DatFileSource.DiskScan),
            entry => chosen = entry);

        // Act
        candidate.ChooseCommand.Execute(null);

        // Assert
        chosen.ShouldBeSameAs(candidate);
    }
}
