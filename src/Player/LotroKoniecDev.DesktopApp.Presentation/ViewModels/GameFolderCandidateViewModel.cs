using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.DesktopApp.Presentation.ViewModels;

/// <summary>One install in the first-run list. A click on it counts as the player's confirmation (ADR-0069 §2).</summary>
public sealed class GameFolderCandidateViewModel
{
    public string Folder { get; }

    public string SourceLabel { get; }

    public string? Warning { get; }

    public bool HasWarning => Warning is not null;

    public DelegateCommand ChooseCommand { get; }

    public GameFolderCandidateViewModel(DatFileLocation location, Action<GameFolderCandidateViewModel> choose)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(choose);

        Folder = GameFolderViewModel.FolderOf(location);
        SourceLabel = GameFolderTexts.SourceLabel(location.Source);
        Warning = location.NeedsConfirmation ? GameFolderTexts.CandidateWarning : null;
        ChooseCommand = new DelegateCommand(() => choose(this), () => true);
    }
}
