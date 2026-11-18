using LotroKoniecDev.Application.Features.Patching;

namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// The patch the launch runs before it starts the game, without the start: it patches when the
/// translation file changed, and otherwise repairs the subfiles a game update replaced (spec 0012
/// Tier 0). The launch and the desktop app's Patch button share it (#42).
/// </summary>
public interface IPatchIfNeededService
{
    Result<PatchIfNeededResponse> PatchIfNeeded(
        string datFilePath,
        string gameVersionFilePath,
        string translationFilePath,
        IProgress<OperationProgress>? progress = null);
}
