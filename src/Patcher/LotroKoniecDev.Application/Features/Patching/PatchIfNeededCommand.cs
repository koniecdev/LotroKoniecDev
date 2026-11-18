using LotroKoniecDev.Application.Abstractions.Messaging;

namespace LotroKoniecDev.Application.Features.Patching;

/// <summary>
/// The launch's patch steps without the launch (#42): a patch when the translation file changed, a
/// repair of replaced subfiles when it did not. It reports to the progress port.
/// </summary>
public sealed record PatchIfNeededCommand(
    string DatFilePath,
    string GameVersionFilePath,
    string TranslationFilePath) : ICommand<Result<PatchIfNeededResponse>>;
