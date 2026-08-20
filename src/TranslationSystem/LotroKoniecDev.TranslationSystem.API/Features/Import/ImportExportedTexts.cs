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
/// Imports a fresh <c>exported.txt</c> against one game version (spec 0001). It streams the file in two
/// passes, so memory use follows one chunk and never the whole file or catalog (spec 0006).
/// Pass 1 validates the upload into a key to hash map and compares it with a streamed compact view of
/// the catalog. It writes nothing, so the mass-removal guard can look at the finished plan first.
/// Pass 2 reads the buffered upload again inside one transaction: new rows go straight into a binary
/// <c>COPY</c>, everything else changes aggregates in chunks of a fixed size, and the version becomes
/// processed with the last save. Either all of it lands or none of it does, and uploading the same
/// file again is safe.
/// </summary>
internal sealed partial class ImportExportedTexts : IEndpoint
{
    /// <summary>
    /// <paramref name="FileStream"/> must support seeking, because the import reads it once per pass.
    /// The endpoint makes sure of that: ASP.NET buffers multipart files, and a stream that cannot seek
    /// is copied to a temp file first.
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
        /// A 79 MB upload full of broken lines is rejected either way. This limit only says how many
        /// line errors are collected for the message (spec 0006).
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

            // Pass 1, the upload side: validate every row while streaming and put it into a key to hash
            // map. The value objects and strings of a row are dropped as soon as they are hashed, so
            // the map is the only thing that grows with the file.
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

            // Pass 1, the catalog side: the diff compares the incoming map with the streamed untracked
            // view and returns a plan of plain values, only ids, keys and counters.
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

            // A domain check before anything is written, so a superseded version still answers 422 and
            // saves nothing. The real change is applied again at the end of the transaction on a freshly
            // loaded instance, because the chunked apply clears the change tracker. This call only
            // proves the change is allowed right now. Imports are admin-only and run one at a time
            // (spec 0001), so the answer cannot change in between.
            Result markProcessedResult = gameVersion.MarkAsProcessed();
            if (markProcessedResult.IsFailure)
            {
                return Result.Failure<ImportSummary>(markProcessedResult.Error);
            }

            // Pass 2 runs in one transaction (spec 0001, ADR-0011). The new rows stream from the
            // buffered upload into a binary COPY on the write context's connection, the other changes
            // update aggregates in fixed-size chunks on that same connection, and the version change
            // commits with the last save.
            // The whole unit runs under the provider's retrying execution strategy and can be run
            // again: every pass seeks the buffered upload back to the start and reloads its chunks, and
            // no tracked state survives into a retry.
            passStopwatch.Restart();
            int supersededVersions = 0;
            await _unitOfWork.ExecuteInTransactionAsync(
                async transactionToken => supersededVersions = await ApplyPlanAsync(plan, command, now, transactionToken),
                cancellationToken);
            long applyPassMilliseconds = passStopwatch.ElapsedMilliseconds;

            // The pass durations are the data behind the "Oś B" async-import decision (spec 0006).
            // Watching them grow toward the request timeout on staging or production is what would
            // justify that ADR.
            LogImportPasses(
                _logger,
                incomingCount, uploadPassMilliseconds, diffPassMilliseconds, applyPassMilliseconds,
                plan.AddedCount, plan.SourceChangedByKey.Count, plan.RemovedIds.Count, plan.RestoredIds.Count, plan.EchoedCount);

            // Processing a version changes which rows are distributed: removed rows drop out and
            // re-added rows come back. So the ready-made translation file is rebuilt after the import
            // commits, because the download endpoint never builds it per request (spec 0001).
            // We schedule the rebuild instead of waiting for it (PERF-04): it walks every row and runs
            // in the background, and several requests in a row cause only one rebuild.
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

                // A single line we cannot parse already rejects the whole upload, because a skipped line
                // looks exactly like a removed row (spec 0001). From here on we keep reading only to
                // collect more errors for the message, not to validate rows.
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
            // The retrying execution strategy may run this again, and a new run has to start with an
            // empty change tracker, so nothing from a rolled-back attempt, such as leftover chunks or
            // the version instance we checked earlier, is saved twice.
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

            // The version's processed flag is saved with the last save of the unit, so it only changes
            // once the whole diff is stored (spec 0001). It is loaded again here because the chunked
            // saves above cleared the tracker. The change was already checked, so a failure now means a
            // rule was broken in code, not a business outcome.
            Maybe<GameVersion> gameVersionMaybe =
                await _gameVersionRepository.GetByIdAsync(command.GameVersionId, cancellationToken);
            GameVersion processedVersion = gameVersionMaybe.Value;
            Result markProcessedResult = processedVersion.MarkAsProcessed();
            if (markProcessedResult.IsFailure)
            {
                throw new InvalidOperationException(
                    $"The game version refused MarkAsProcessed inside the apply transaction after passing the pre-check: {markProcessedResult.Error.Message}");
            }

            // Older versions that piled up never get an upload of their own (spec 0001). Processing the
            // newest one supersedes every unprocessed version detected before it, saved with the same
            // final save, so it lands together with the diff.
            // That is what protects us from an old export: a later import against one of those versions
            // fails MarkAsProcessed with SupersededCannotBeProcessed instead of rolling the catalog
            // back. All these rows are Unprocessed, because the repository filters them, so
            // MarkSuperseded can only fail on a broken rule, handled like the change above.
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
        /// Pass 2 reading the buffered upload again. Every line already passed pass 1, and the buffered
        /// file cannot change in between, so a parse or value-object failure here means a broken rule in
        /// code. <see cref="Result{T}.Value"/>'s guard surfaces it; there is no per-row handling.
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
                Echoed: plan.EchoedCount,
                Warnings: warnings);
        }

        [LoggerMessage(EventId = EventIds.ImportPassesCompleted, Level = LogLevel.Information, Message = "Import passes for {IncomingRows} incoming row(s): upload pass {UploadPassMs} ms, diff pass {DiffPassMs} ms, apply pass {ApplyPassMs} ms (added {Added}, source-changed {SourceChanged}, removed {Removed}, restored {Restored}, echoed {Echoed}).")]
        private static partial void LogImportPasses(ILogger logger, int incomingRows, long uploadPassMs, long diffPassMs, long applyPassMs, int added, int sourceChanged, int removed, int restored, int echoed);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        // The exported.txt is about 80 MB and keeps growing, so this one endpoint replaces Kestrel's
        // 30 MB default body limit with the configured one (spec 0003, #208). There is no request
        // timeout on the server: a full-catalog import really does take minutes, and the only timeout
        // that had to be raised was the Frontend's, in HttpClientsDependencyInjectionExtensions.
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

                // The two-pass import reads the upload twice (spec 0006). ASP.NET buffers multipart
                // files, in memory below 64 KB and in a temp file above, so the form-file stream can
                // seek. The copy below is only a fallback in case a host ever hands us a stream that
                // can only be read forward.
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
