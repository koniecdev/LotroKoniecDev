using Mediator;

namespace LotroKoniecDev.Application.Features.GameLaunching;

public sealed record GameLaunchingCommand(
    string DatFilePath,
    string GameVersionFilePath) : ICommand<Result<GameLaunchingResponse>>;
