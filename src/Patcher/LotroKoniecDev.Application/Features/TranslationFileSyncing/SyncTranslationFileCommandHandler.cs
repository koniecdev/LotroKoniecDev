using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

internal sealed class SyncTranslationFileCommandHandler
    : ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>>
{
    private readonly ITranslationFileDownloader _downloader;
    private readonly ITranslationFileCache _cache;
    private readonly IValidator<SyncTranslationFileCommand> _validator;

    public SyncTranslationFileCommandHandler(
        ITranslationFileDownloader downloader,
        ITranslationFileCache cache,
        IValidator<SyncTranslationFileCommand> validator)
    {
        _downloader = downloader;
        _cache = cache;
        _validator = validator;
    }

    public async ValueTask<Result<TranslationFileSyncResponse>> Handle(
        SyncTranslationFileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ValidationResult validationResult = _validator.Validate(command);
        if (!validationResult.IsValid)
        {
            return Result.Failure<TranslationFileSyncResponse>(
                validationResult.ToValidationError(nameof(SyncTranslationFileCommand)));
        }

        string? currentETag = _cache.ReadETag(command.TranslationFilePath);

        Result<TranslationFileFetchResult> fetchResult =
            await _downloader.FetchAsync(command.TmsBaseUrl, currentETag, cancellationToken);

        if (fetchResult.IsFailure)
        {
            // The launch must never block on the network (spec 0001 Q5): a failed fetch falls back to
            // the local translation file. Whether one actually exists is the launch path's concern
            // (it reports a missing file), not the sync's — so the network stays strictly best-effort.
            // A rejected download (integrity check, AUDIT-SEC-01) gets its own outcome so the report
            // says the file was refused, not that the server was unreachable.
            TranslationFileSyncOutcome outcome =
                fetchResult.Error.Code == DomainErrors.TranslationFileSync.IntegrityCheckFailedCode
                    ? TranslationFileSyncOutcome.IntegrityCheckFailedUsedCache
                    : TranslationFileSyncOutcome.OfflineUsedCache;

            return Result.Success(new TranslationFileSyncResponse(outcome, fetchResult.Error.Message));
        }

        TranslationFileFetchResult fetch = fetchResult.Value;
        if (!fetch.IsModified)
        {
            return Result.Success(new TranslationFileSyncResponse(TranslationFileSyncOutcome.UpToDate, null));
        }

        Result save = _cache.Save(command.TranslationFilePath, fetch.Content!, fetch.ETag!);
        if (save.IsFailure)
        {
            return Result.Failure<TranslationFileSyncResponse>(save.Error);
        }

        return Result.Success(new TranslationFileSyncResponse(TranslationFileSyncOutcome.Updated, null));
    }
}
