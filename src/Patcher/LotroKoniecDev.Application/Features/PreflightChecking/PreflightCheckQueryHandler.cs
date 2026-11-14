using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Domain.Core.BuildingBlocks;
using LotroKoniecDev.Primitives.Enums;

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
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.DatFilePath))
        {
            return Result.Failure<PreflightReportResponse>(
                Error.Validation($"{nameof(PreflightCheckQuery)}.Validation", "DatFilePath must not be empty."));
        }

        if (string.IsNullOrWhiteSpace(query.VersionFilePath))
        {
            return Result.Failure<PreflightReportResponse>(
                Error.Validation($"{nameof(PreflightCheckQuery)}.Validation", "VersionFilePath must not be empty."));
        }

        bool isGameRunning = _gameProcessDetector.IsLotroRunning();

        string? directory = Path.GetDirectoryName(query.DatFilePath);
        bool hasWriteAccess = directory is not null && _writeAccessChecker.CanWriteTo(directory);

        // The file check runs only when the game is closed and the folder is writable. It opens the
        // live DAT, which the launcher writes while it runs.
        DatFileWriteCheck datFileWriteCheck = !isGameRunning && hasWriteAccess
            ? _writeAccessChecker.CheckFile(query.DatFilePath)
            : new DatFileWriteCheck(DatFileWriteRefusal.None);

        Result<GameUpdateCheckSummary> gameUpdateCheckSummaryResult =
            await _gameUpdateChecker.CheckForUpdateAsync(query.VersionFilePath);

        return Result.Success(new PreflightReportResponse(
            isGameRunning, hasWriteAccess, gameUpdateCheckSummaryResult, datFileWriteCheck));
    }
}
