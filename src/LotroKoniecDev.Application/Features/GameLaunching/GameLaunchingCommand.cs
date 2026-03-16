using Mediator;

namespace LotroKoniecDev.Application.Features.GameLaunching;

public sealed record GameLaunchingCommand(
    string DatFilePath,
    string GameVersionFilePath,
    string TranslationFilePath,
    bool UseLegacyFlow = false) : ICommand<Result<GameLaunchingResponse>>;
