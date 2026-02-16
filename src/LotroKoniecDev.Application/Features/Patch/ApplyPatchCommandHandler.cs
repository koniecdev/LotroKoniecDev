using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Domain.Core.Errors;
using LotroKoniecDev.Domain.Core.Monads;
using Mediator;

namespace LotroKoniecDev.Application.Features.Patch;

internal sealed class ApplyPatchCommandHandler : ICommandHandler<ApplyPatchCommand, Result<PatchSummaryResponse>>
{
    private readonly IOperationStatusReporter _operationStatusReporter;
    private readonly IFileProvider _fileProvider;
    private readonly IPatcher _patcher;
    private readonly IBackupManager _backupManager;
    private readonly IPreflightChecker _preflightChecker;

    public ApplyPatchCommandHandler(
        IOperationStatusReporter operationStatusReporter,
        IFileProvider fileProvider,
        IPatcher patcher,
        IBackupManager backupManager,
        IPreflightChecker preflightChecker)
    {
        _operationStatusReporter = operationStatusReporter;
        _fileProvider = fileProvider;
        _patcher = patcher;
        _backupManager = backupManager;
        _preflightChecker = preflightChecker;
    }

    public async ValueTask<Result<PatchSummaryResponse>> Handle(ApplyPatchCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_fileProvider.Exists(command.TranslationsPath))
        {
            return Result.Failure<PatchSummaryResponse>(
                DomainErrors.Translation.FileNotFound(command.TranslationsPath));
        }

        if (!_fileProvider.Exists(command.DatFilePath))
        {
            return Result.Failure<PatchSummaryResponse>(
                DomainErrors.DatFile.NotFound(command.DatFilePath));
        }

        if (!await _preflightChecker.RunAllAsync(command.DatFilePath, command.VersionFilePath))
        {
            return Result.Failure<PatchSummaryResponse>(
                DomainErrors.DatFileLocation.GameRunning);
        }

        Result backupResult = _backupManager.Create(command.DatFilePath);
        if (backupResult.IsFailure)
        {
            return Result.Failure<PatchSummaryResponse>(backupResult.Error);
        }

        _operationStatusReporter.Report($"Loading translations from: {command.TranslationsPath}");

        Result<PatchSummaryResponse> patchResult = _patcher.ApplyTranslations(
            translationsPath: command.TranslationsPath,
            datFilePath: command.DatFilePath,
            progress: (applied, total) => command.Progress?.Report(new OperationProgress(applied, total)));

        if (patchResult.IsFailure)
        {
            _backupManager.Restore(command.DatFilePath);
            return patchResult;
        }

        PatchSummaryResponse summary = patchResult.Value;

        foreach (string warning in summary.Warnings)
        {
            _operationStatusReporter.Report(warning);
        }

        if (summary.SkippedTranslations > 0)
        {
            _operationStatusReporter.Report($"Skipped {summary.SkippedTranslations} translations");
        }

        return Result.Success(summary);
    }
}
