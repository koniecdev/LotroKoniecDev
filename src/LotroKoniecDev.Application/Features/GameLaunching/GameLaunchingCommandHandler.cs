using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Domain.Models;
using Mediator;

namespace LotroKoniecDev.Application.Features.GameLaunching;

internal sealed class GameLaunchingCommandHandler : ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>
{
    private readonly IGameUpdateChecker _gameUpdateChecker;
    private readonly IDatVersionReader _datVersionReader;

    public GameLaunchingCommandHandler(
        IGameUpdateChecker gameUpdateChecker,
        IDatVersionReader datVersionReader)
    {
        _gameUpdateChecker = gameUpdateChecker;
        _datVersionReader = datVersionReader;
    }
    
    public async ValueTask<Result<GameLaunchingResponse>> Handle(GameLaunchingCommand command, CancellationToken cancellationToken)
    {
        Result<GameUpdateCheckSummary> checkForUpdateResult =
            await _gameUpdateChecker.CheckForUpdateAsync(command.GameVersionFilePath);
        if (checkForUpdateResult.IsFailure)
        {
            return Result.Failure<GameLaunchingResponse>(checkForUpdateResult.Error);
        }
        
        GameUpdateCheckSummary gameUpdateCheckSummary = checkForUpdateResult.Value;

        if (!gameUpdateCheckSummary.UpdateDetected)
        {
                
        }
        
        if (gameUpdateCheckSummary.UpdateDetected)
        {
            Result<DatVersionInfo> beforeUpdateDatVersionResult = _datVersionReader.ReadVersion(command.DatFilePath);
            if (beforeUpdateDatVersionResult.IsFailure)
            {
                return Result.Failure<GameLaunchingResponse>(beforeUpdateDatVersionResult.Error);
            }

            if (gameUpdateCheckSummary.IsFirstLaunch)
            {
                //first time running the app, no version file yet
                //read dat
                //launch the game
                //wait for exit - or game process - and kill it
                // re-read dat to ensure it has changed
                //if it changed, fetch forum version and save it
                //otherwise, re-launch the game unless update is not done (dat change after killing lotro launcher process)

            }
        }

        // TODO: implement normal launch + update pipelines
        throw new NotImplementedException();
    }
}
