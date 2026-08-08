using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Domain.Core.Errors;
using Microsoft.Extensions.Logging;

namespace LotroKoniecDev.Application.Features.TranslationFileSyncing;

internal sealed partial class SyncTranslationFileCommandHandler
    : ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>>
{
    private readonly ITranslationFileEndpointResolver _endpointResolver;
    private readonly ITranslationFileDownloader _downloader;
    private readonly ITranslationFileCache _cache;
    private readonly IValidator<SyncTranslationFileCommand> _validator;
    private readonly ILogger<SyncTranslationFileCommandHandler> _logger;

    public SyncTranslationFileCommandHandler(
        ITranslationFileEndpointResolver endpointResolver,
        ITranslationFileDownloader downloader,
        ITranslationFileCache cache,
        IValidator<SyncTranslationFileCommand> validator,
        ILogger<SyncTranslationFileCommandHandler> logger)
    {
        _endpointResolver = endpointResolver;
        _downloader = downloader;
        _cache = cache;
        _validator = validator;
        _logger = logger;
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

        string? cachedEndpointHref = _cache.ReadEndpointHref(command.TranslationFilePath);

        // Every entry point is resolved by rel from the service document (ADR-0041 / #611); the CLI
        // owns one root URL and nothing else. A resolution that fails is treated like an unreachable
        // server — reported, never fatal — because the launch must not block on the network
        // (spec 0001 Q5) and nothing here may fall back to a guessed path.
        Result<Uri> endpointResult =
            await _endpointResolver.ResolveAsync(command.TmsBaseUrl, cachedEndpointHref, cancellationToken);

        if (endpointResult.IsFailure)
        {
            return Result.Success(new TranslationFileSyncResponse(
                TranslationFileSyncOutcome.EndpointUnresolvedUsedCache, endpointResult.Error.Message));
        }

        Uri endpoint = endpointResult.Value;
        string? currentETag = _cache.ReadETag(command.TranslationFilePath);

        Result<TranslationFileFetchResult> fetchResult =
            await _downloader.FetchAsync(endpoint, currentETag, cancellationToken);

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

        RememberEndpoint(command.TranslationFilePath, endpoint, cachedEndpointHref);

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

    /// <summary>
    /// Records the endpoint that just served the file as the new last-known-good. Only a change is
    /// written, so the steady state (an unchanged href answering 304) touches no disk at all.
    /// <para>
    /// A failed write is logged and swallowed on purpose: this sidecar is a <i>hint</i> for a future
    /// outage, and the next run simply re-resolves it from discovery. Failing the sync over it would
    /// block the launch on an unwritable cache directory — the same class of harm as blocking it on
    /// the network, which spec 0001 Q5 forbids. A failed <see cref="ITranslationFileCache.Save"/> is
    /// different and still fatal: there the downloaded file itself was lost.
    /// </para>
    /// </summary>
    private void RememberEndpoint(string translationFilePath, Uri endpoint, string? cachedEndpointHref)
    {
        string href = endpoint.ToString();
        if (string.Equals(href, cachedEndpointHref, StringComparison.Ordinal))
        {
            return;
        }

        Result save = _cache.SaveEndpointHref(translationFilePath, href);
        if (save.IsFailure)
        {
            LogEndpointHintNotPersisted(_logger, href, save.Error.Message);
        }
    }

    [LoggerMessage(
        EventId = EventIds.TranslationFileEndpointHintNotPersisted,
        Level = LogLevel.Warning,
        Message = "Could not persist the translation-file endpoint hint {Endpoint} ({Error}); "
                  + "the sync continues and re-resolves it from discovery next run")]
    private static partial void LogEndpointHintNotPersisted(ILogger logger, string endpoint, string error);
}
