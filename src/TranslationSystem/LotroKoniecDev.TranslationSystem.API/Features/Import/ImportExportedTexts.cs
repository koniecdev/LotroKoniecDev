using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.Bulk;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.Import;

/// <summary>
/// Version-bound import of a fresh <c>exported.txt</c> (spec 0001), streamed in two passes so the
/// working set scales with a chunk, never the file or the catalog (spec 0006). Pass 1 validates
/// the streamed upload into a key→hash map and diffs it against a streamed compact catalog
/// projection — writing nothing, so the mass-removal guard runs on the full plan first. Pass 2
/// re-streams the buffered upload inside one transaction: added rows go straight into the binary
/// <c>COPY</c>, everything else mutates aggregates in bounded chunks, and the version flips to
/// processed with the last save (all-or-nothing, idempotent re-upload).
/// </summary>
internal sealed partial class ImportExportedTexts : IEndpoint
{
    /// <summary>
    /// <paramref name="FileStream"/> must be seekable — the import reads it once per pass. The
    /// endpoint guarantees it (ASP.NET buffers multipart files; a non-seekable stream is copied to
    /// a temp file first).
    /// </summary>
    internal sealed record Command(GameVersionId GameVersionId, Stream FileStream, bool AllowMassRemoval)
        : ICommand<Result<ImportSummary>>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.GameVersionId)
                .Must(id => id.Value != Guid.Empty)
                .WithMessage("Game version id is required.");

            RuleFor(command => command.FileStream)
                .NotNull();
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result<ImportSummary>>
    {
        /// <summary>
        /// An all-garbage 79 MB upload is rejected either way; the cap only bounds how many line
        /// errors are collected for the rejection message (spec 0006).
        /// </summary>
        private const int MaxCollectedParseErrors = 100;

        private readonly IValidator<Command> _validator;
        private readonly ITranslationExportParser _parser;
        private readonly IGameVersionRepository _gameVersionRepository;
        private readonly ITranslationRepository _translationRepository;
        private readonly IBulkTranslationInserter _bulkInserter;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;
        private readonly ImportSettings _settings;
        private readonly ITranslationFileRebuildScheduler _rebuildScheduler;
        private readonly ILogger<Handler> _logger;

        public Handler(
            IValidator<Command> validator,
            ITranslationExportParser parser,
            IGameVersionRepository gameVersionRepository,
            ITranslationRepository translationRepository,
            IBulkTranslationInserter bulkInserter,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider,
            IOptions<ImportSettings> settings,
            ITranslationFileRebuildScheduler rebuildScheduler,
            ILogger<Handler> logger)
        {
            _validator = validator;
            _parser = parser;
            _gameVersionRepository = gameVersionRepository;
            _translationRepository = translationRepository;
            _bulkInserter = bulkInserter;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
            _settings = settings.Value;
            _rebuildScheduler = rebuildScheduler;
            _logger = logger;
        }

        public async ValueTask<Result<ImportSummary>> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure<ImportSummary>(new Error("Import.Validation", message, TypeOfError.Validation));
            }

            Maybe<GameVersion> gameVersionMaybe = await _gameVersionRepository.GetByIdAsync(command.GameVersionId, cancellationToken);
            if (gameVersionMaybe.HasNoValue)
            {
                return Result.Failure<ImportSummary>(DomainErrors.GameVersionEntity.NotFound(command.GameVersionId));
            }

            GameVersion gameVersion = gameVersionMaybe.Value;

            // Pass 1, upload side: stream-validate every row into a key→hash map — per-row VOs and
            // strings are discarded as soon as they are hashed, so the map is the only thing that
            // scales with the file.
            Stopwatch passStopwatch = Stopwatch.StartNew();
            Result<Dictionary<FragmentKeyValue, SourceHash>> incomingResult =
                await BuildIncomingMapAsync(command.FileStream, cancellationToken);
            if (incomingResult.IsFailure)
            {
                return Result.Failure<ImportSummary>(incomingResult.Error);
            }

            Dictionary<FragmentKeyValue, SourceHash> incomingByKey = incomingResult.Value;
            if (incomingByKey.Count == 0)
            {
                return Result.Failure<ImportSummary>(ImportErrors.EmptyUpload());
            }

            long uploadPassMilliseconds = passStopwatch.ElapsedMilliseconds;
            int incomingCount = incomingByKey.Count;

            DateTimeOffset now = _timeProvider.GetUtcNow();

            // Pass 1, catalog side: the diff consumes the incoming map against the streamed
            // untracked projection and returns a value-row plan (ids, keys and counters only).
            passStopwatch.Restart();
            TranslationDiffPlan plan = await TranslationDiffService.ComputePlanAsync(
                _translationRepository.StreamSourceDigestsAsync(cancellationToken),
                incomingByKey,
                cancellationToken);
            long diffPassMilliseconds = passStopwatch.ElapsedMilliseconds;

            if (!command.AllowMassRemoval && plan.RemovedFraction > _settings.MaxRemovedFractionWithoutOverride)
            {
                return Result.Failure<ImportSummary>(
                    ImportErrors.MassRemovalBlocked(
                        plan.RemovedIds.Count,
                        plan.ComparableExistingCount,
                        plan.RemovedFraction,
                        _settings.MaxRemovedFractionWithoutOverride));
            }

            // Domain pre-check before any write (a superseded version keeps returning 422 with
            // nothing persisted). The persisted flip is re-applied at the end of the transaction on
            // a freshly tracked instance, because the chunked apply clears the change tracker —
            // this call only proves the transition is legal now. Imports are admin-only and serial
            // (spec 0001), so the rule cannot change between here and the apply.
            Result markProcessedResult = gameVersion.MarkAsProcessed();
            if (markProcessedResult.IsFailure)
            {
                return Result.Failure<ImportSummary>(markProcessedResult.Error);
            }

            // Pass 2 — one atomic transaction (spec 0001, ADR-0011): the added rows stream from the
            // buffered upload into a binary COPY on the write context's connection, the remaining
            // outcomes mutate aggregates in bounded chunks on that same connection, and the version
            // flip commits with the last save. The unit runs under the provider's retrying
            // execution strategy and is re-entrant: every pass re-seeks the buffered upload and
            // reloads its chunks, and no tracked state survives into a retry.
            passStopwatch.Restart();
            int supersededVersions = 0;
            await _unitOfWork.ExecuteInTransactionAsync(
                async transactionToken => supersededVersions = await ApplyPlanAsync(plan, command, now, transactionToken),
                cancellationToken);
            long applyPassMilliseconds = passStopwatch.ElapsedMilliseconds;

            // Pass durations are the "Oś B" (async-import) trigger data (spec 0006): watching them
            // grow toward the request budget on staging/prod is what would justify that ADR.
            LogImportPasses(
                _logger,
                incomingCount, uploadPassMilliseconds, diffPassMilliseconds, applyPassMilliseconds,
                plan.AddedCount, plan.SourceChangedByKey.Count, plan.RemovedIds.Count, plan.RestoredIds.Count);

            // Version processing changes the distributed set (removed rows drop out, re-added rows
            // return), so the pre-built translation file is regenerated after the import commits
            // (spec 0001: the download endpoint never builds per-request). Scheduled, not awaited
            // (PERF-04): the O(N) rebuild runs debounced in the background on the host lifetime.
            _rebuildScheduler.Schedule(SupportedLanguages.Polish);

            return Result.Success(BuildSummary(plan, supersededVersions));
        }

        private async Task<Result<Dictionary<FragmentKeyValue, SourceHash>>> BuildIncomingMapAsync(
            Stream fileStream,
            CancellationToken cancellationToken)
        {
            fileStream.Seek(0, SeekOrigin.Begin);

            Dictionary<FragmentKeyValue, SourceHash> incomingByKey = [];
            List<ExportParseError> parseErrors = [];

            await foreach (ParsedExportLine line in _parser.ParseLinesAsync(fileStream, cancellationToken))
            {
                if (line.Error is { } parseError)
                {
                    parseErrors.Add(parseError);
                    if (parseErrors.Count == MaxCollectedParseErrors)
                    {
                        break;
                    }

                    continue;
                }

                // One unparseable line already rejects the whole upload (spec 0001: a skipped line
                // is indistinguishable from a removed row) — keep scanning only to collect more
                // parse errors for the message, not to validate rows.
                if (parseErrors.Count > 0)
                {
                    continue;
                }

                ParsedExportRow row = line.Row!;

                Result<FragmentKey> keyResult = FragmentKey.Create(row.FileId, row.GossipId);
                if (keyResult.IsFailure)
                {
                    return Result.Failure<Dictionary<FragmentKeyValue, SourceHash>>(
                        ImportErrors.InvalidRow(row.FileId, row.GossipId, keyResult.Error.Message));
                }

                Result<TranslationSource> sourceResult = TranslationSource.Create(row.Content, row.ArgsOrder, row.ArgsId);
                if (sourceResult.IsFailure)
                {
                    return Result.Failure<Dictionary<FragmentKeyValue, SourceHash>>(
                        ImportErrors.InvalidRow(row.FileId, row.GossipId, sourceResult.Error.Message));
                }

                if (!incomingByKey.TryAdd(FragmentKeyValue.From(keyResult.Value), SourceHash.Compute(sourceResult.Value)))
                {
                    return Result.Failure<Dictionary<FragmentKeyValue, SourceHash>>(
                        ImportErrors.DuplicateFragmentKey(row.FileId, row.GossipId));
                }
            }

            if (parseErrors.Count > 0)
            {
                return Result.Failure<Dictionary<FragmentKeyValue, SourceHash>>(
                    ImportErrors.ParseFailed(parseErrors.Count, parseErrors[0]));
            }

            return Result.Success(incomingByKey);
        }

        private async Task<int> ApplyPlanAsync(
            TranslationDiffPlan plan,
            Command command,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            // Re-entrancy under the retrying execution strategy: a re-run must start from a clean
            // tracker so nothing from a rolled-back attempt (chunk leftovers, the pre-checked
            // version instance) is saved twice.
            _unitOfWork.ClearChangeTracker();

            if (plan.AddedCount > 0)
            {
                command.FileStream.Seek(0, SeekOrigin.Begin);
                await _bulkInserter.InsertAsync(
                    StreamAddedRowsAsync(plan, command.FileStream, command.GameVersionId, now, cancellationToken),
                    cancellationToken);
            }

            if (plan.SourceChangedByKey.Count > 0)
            {
                await ApplySourceChangesAsync(plan, command, now, cancellationToken);
            }

            await ApplyByIdsInChunksAsync(
                plan.RemovedIds,
                translation => translation.MarkRemoved(command.GameVersionId, now),
                cancellationToken);

            await ApplyByIdsInChunksAsync(
                plan.RestoredIds,
                translation => translation.Restore(now),
                cancellationToken);

            // The version's processed flag commits with the unit's final save, so IsProcessed flips
            // only after the whole diff is durable (spec 0001). Loaded fresh here because the
            // chunked saves above cleared the tracker; the transition was pre-checked, so a failure
            // now is an invariant break, not a business outcome.
            Maybe<GameVersion> gameVersionMaybe =
                await _gameVersionRepository.GetByIdAsync(command.GameVersionId, cancellationToken);
            GameVersion processedVersion = gameVersionMaybe.Value;
            Result markProcessedResult = processedVersion.MarkAsProcessed();
            if (markProcessedResult.IsFailure)
            {
                throw new InvalidOperationException(
                    $"The game version refused MarkAsProcessed inside the apply transaction after passing the pre-check: {markProcessedResult.Error.Message}");
            }

            // Stacked older versions never get their own upload (spec 0001): processing the newest
            // supersedes every still-unprocessed version detected before it, committed with the same
            // final save (all-or-nothing with the diff). This is what arms the stale-export guard — a
            // later import against one of them then fails MarkAsProcessed with
            // SupersededCannotBeProcessed instead of rewinding the catalog backwards. The rows are all
            // Unprocessed (repository filter), so MarkSuperseded can only fail on an invariant break,
            // handled like the flip above.
            IReadOnlyList<GameVersion> olderUnprocessedVersions =
                await _gameVersionRepository.GetUnprocessedDetectedBeforeAsync(processedVersion.DetectedAt, cancellationToken);
            foreach (GameVersion olderVersion in olderUnprocessedVersions)
            {
                Result markSupersededResult = olderVersion.MarkSuperseded();
                if (markSupersededResult.IsFailure)
                {
                    throw new InvalidOperationException(
                        $"An unprocessed game version refused MarkSuperseded inside the apply transaction: {markSupersededResult.Error.Message}");
                }
            }

            return olderUnprocessedVersions.Count;
        }

        private async IAsyncEnumerable<Translation> StreamAddedRowsAsync(
            TranslationDiffPlan plan,
            Stream fileStream,
            GameVersionId gameVersionId,
            DateTimeOffset now,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach ((FragmentKey key, TranslationSource source) in StreamValidatedRowsAsync(fileStream, cancellationToken))
            {
                if (!plan.IsAdded(FragmentKeyValue.From(key)))
                {
                    continue;
                }

                yield return Translation.CreateUntranslated(key, source, gameVersionId, now).Value;
            }
        }

        private async Task ApplySourceChangesAsync(
            TranslationDiffPlan plan,
            Command command,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            command.FileStream.Seek(0, SeekOrigin.Begin);

            List<(TranslationId Id, TranslationSource NewSource)> chunk = new(_settings.ApplyChunkSize);

            await foreach ((FragmentKey key, TranslationSource source) in StreamValidatedRowsAsync(command.FileStream, cancellationToken))
            {
                if (!plan.SourceChangedByKey.TryGetValue(FragmentKeyValue.From(key), out TranslationId translationId))
                {
                    continue;
                }

                chunk.Add((translationId, source));
                if (chunk.Count == _settings.ApplyChunkSize)
                {
                    await ApplySourceChangeChunkAsync(chunk, command.GameVersionId, now, cancellationToken);
                    chunk.Clear();
                }
            }

            if (chunk.Count > 0)
            {
                await ApplySourceChangeChunkAsync(chunk, command.GameVersionId, now, cancellationToken);
            }
        }

        private async Task ApplySourceChangeChunkAsync(
            List<(TranslationId Id, TranslationSource NewSource)> chunk,
            GameVersionId gameVersionId,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            List<TranslationId> ids = chunk.Select(pair => pair.Id).ToList();
            IReadOnlyList<Translation> translations = await _translationRepository.GetByIdsAsync(ids, cancellationToken);
            Dictionary<TranslationId, Translation> translationsById = translations.ToDictionary(translation => translation.Id);

            foreach ((TranslationId id, TranslationSource newSource) in chunk)
            {
                translationsById[id].ApplySourceChange(newSource, gameVersionId, now);
            }

            await _unitOfWork.SaveChangesAndClearAsync(cancellationToken);
        }

        private async Task ApplyByIdsInChunksAsync(
            IReadOnlyList<TranslationId> ids,
            Action<Translation> mutate,
            CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < ids.Count; offset += _settings.ApplyChunkSize)
            {
                int chunkSize = Math.Min(_settings.ApplyChunkSize, ids.Count - offset);
                List<TranslationId> chunk = new(chunkSize);
                for (int index = 0; index < chunkSize; index++)
                {
                    chunk.Add(ids[offset + index]);
                }

                IReadOnlyList<Translation> translations = await _translationRepository.GetByIdsAsync(chunk, cancellationToken);
                foreach (Translation translation in translations)
                {
                    mutate(translation);
                }

                await _unitOfWork.SaveChangesAndClearAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Pass 2's re-read of the buffered upload: every line already passed Pass 1, so a parse or
        /// VO failure here is an invariant break (the buffered file cannot change between passes),
        /// surfaced by <see cref="Result{T}.Value"/>'s guard, not per-row handling.
        /// </summary>
        private async IAsyncEnumerable<(FragmentKey Key, TranslationSource Source)> StreamValidatedRowsAsync(
            Stream fileStream,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (ParsedExportLine line in _parser.ParseLinesAsync(fileStream, cancellationToken))
            {
                if (line.Row is not { } row)
                {
                    throw new InvalidOperationException(
                        $"The upload failed to re-parse during apply after passing Pass 1 (line {line.Error!.LineNumber}: {line.Error.Message}).");
                }

                yield return (
                    FragmentKey.Create(row.FileId, row.GossipId).Value,
                    TranslationSource.Create(row.Content, row.ArgsOrder, row.ArgsId).Value);
            }
        }

        private static ImportSummary BuildSummary(TranslationDiffPlan plan, int supersededVersions)
        {
            List<string> warnings = [];
            if (plan.RestoredIds.Count > 0)
            {
                warnings.Add($"{plan.RestoredIds.Count} previously-removed row(s) re-added with an unchanged source and restored.");
            }

            if (supersededVersions > 0)
            {
                warnings.Add($"{supersededVersions} older unprocessed version(s) marked superseded — they will never receive their own upload.");
            }

            return new ImportSummary(
                Added: plan.AddedCount,
                SourceChanged: plan.SourceChangedByKey.Count,
                Invalidated: plan.InvalidatedCount,
                Removed: plan.RemovedIds.Count,
                Unchanged: plan.UnchangedCount,
                Warnings: warnings);
        }

        [LoggerMessage(EventId = EventIds.ImportPassesCompleted, Level = LogLevel.Information, Message = "Import passes for {IncomingRows} incoming row(s): upload pass {UploadPassMs} ms, diff pass {DiffPassMs} ms, apply pass {ApplyPassMs} ms (added {Added}, source-changed {SourceChanged}, removed {Removed}, restored {Restored}).")]
        private static partial void LogImportPasses(ILogger logger, int incomingRows, long uploadPassMs, long diffPassMs, long applyPassMs, int added, int sourceChanged, int removed, int restored);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // The exported.txt is ~80 MB and grows, so this single endpoint overrides Kestrel's 30 MB
        // default body cap with the configured ceiling (spec 0003, #208). No server-side request
        // timeout is added: a full-catalog import legitimately runs for minutes, and the only client
        // budget that needed lifting is the Frontend's (HttpClientsDependencyInjectionExtensions).
        long maxUploadBytes = endpointRouteBuilder.ServiceProvider
            .GetRequiredService<IOptions<ImportSettings>>().Value.MaxUploadBytes;

        endpointRouteBuilder.MapPost("/api/v1/game-versions/{id:guid}/import", async (
                Guid id,
                IFormFile file,
                ICommandHandler<Command, Result<ImportSummary>> handler,
                CancellationToken cancellationToken,
                [FromQuery] bool allowMassRemoval = false) =>
            {
                await using Stream stream = file.OpenReadStream();

                // The two-pass import re-reads the upload (spec 0006). ASP.NET buffers multipart
                // files (memory below 64 KB, temp file above), so the form-file stream is seekable;
                // the copy is a belt-and-braces fallback should a host ever hand out a forward-only
                // stream.
                if (stream.CanSeek)
                {
                    Command command = new(GameVersionId.Create(id), stream, allowMassRemoval);
                    Result<ImportSummary> result = await handler.Handle(command, cancellationToken);

                    return result.IsSuccess
                        ? Results.Ok(result.Value)
                        : Results.Problem(result.Error.ToProblemDetails());
                }

                await using FileStream buffered = new(
                    Path.GetTempFileName(),
                    FileMode.Create,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 81_920,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await stream.CopyToAsync(buffered, cancellationToken);

                Command bufferedCommand = new(GameVersionId.Create(id), buffered, allowMassRemoval);
                Result<ImportSummary> bufferedResult = await handler.Handle(bufferedCommand, cancellationToken);

                return bufferedResult.IsSuccess
                    ? Results.Ok(bufferedResult.Value)
                    : Results.Problem(bufferedResult.Error.ToProblemDetails());
            })
            .WithName(nameof(ImportExportedTexts))
            .WithTags("Import")
            .RequireAuthorization(AuthorizationPolicies.RequireAdminRole)
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(maxUploadBytes))
            .Produces<ImportSummary>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
