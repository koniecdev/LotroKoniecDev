using LotroKoniecDev.Application.Abstractions;
using Mediator;

namespace LotroKoniecDev.Application.Features.PreflightChecking;

internal sealed class PreflightCheckQueryHandler : IQueryHandler<PreflightCheckQuery, Result<PreflightReportResponse>>
{
    private readonly IGameUpdateChecker _gameUpdateChecker;
    private readonly IGameProcessDetector _gameProcessDetector;
    private readonly IWriteAccessChecker _writeAccessChecker;

    public PreflightCheckQueryHandler(
        IGameUpdateChecker gameUpdateChecker,
        IGameProcessDetector gameProcessDetector,
        IWriteAccessChecker writeAccessChecker)
    {
        _gameUpdateChecker = gameUpdateChecker;
        _gameProcessDetector = gameProcessDetector;
        _writeAccessChecker = writeAccessChecker;
    }
    
    public async ValueTask<Result<PreflightReportResponse>> Handle(PreflightCheckQuery query, CancellationToken cancellationToken)
    {
        bool isGameRunning = _gameProcessDetector.IsLotroRunning();
        
        string? directory = Path.GetDirectoryName(query.DatFilePath);
        bool hasWriteAccess = directory is not null && _writeAccessChecker.CanWriteTo(directory);
        
        Result<GameUpdateCheckSummary> gameUpdateCheckSummaryResult = 
            await _gameUpdateChecker.CheckForUpdateAsync(query.VersionFilePath);
        
        return Result.Success(new PreflightReportResponse(isGameRunning, hasWriteAccess, gameUpdateCheckSummaryResult));
    }
}
