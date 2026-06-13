using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Parsing;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Services;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.Import;

/// <summary>
/// Version-bound import of a fresh <c>exported.txt</c> (spec 0001): parse, diff against the stored
/// source state by <c>(FileId, GossipId)</c>, apply the five outcomes, flip the version to
/// processed — all in a single transaction (all-or-nothing, idempotent re-upload).
/// </summary>
internal sealed class ImportExportedTexts : IEndpoint
{
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

    internal sealed class Handler : ICommandHandler<Command, Result<ImportSummary>>
    {
        private readonly IValidator<Command> _validator;
        private readonly ITranslationExportParser _parser;
        private readonly IGameVersionRepository _gameVersionRepository;
        private readonly ITranslationRepository _translationRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _timeProvider;
        private readonly ImportSettings _settings;

        public Handler(
            IValidator<Command> validator,
            ITranslationExportParser parser,
            IGameVersionRepository gameVersionRepository,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork,
            TimeProvider timeProvider,
            IOptions<ImportSettings> settings)
        {
            _validator = validator;
            _parser = parser;
            _gameVersionRepository = gameVersionRepository;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
            _timeProvider = timeProvider;
            _settings = settings.Value;
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

            ParsedExport parsed = await _parser.ParseAsync(command.FileStream, cancellationToken);
            if (parsed.HasErrors)
            {
                return Result.Failure<ImportSummary>(ImportErrors.ParseFailed(parsed.Errors.Count, parsed.Errors[0]));
            }

            if (parsed.Rows.Count == 0)
            {
                return Result.Failure<ImportSummary>(ImportErrors.EmptyUpload());
            }

            Result<IReadOnlyList<IncomingTranslation>> incomingResult = MapToIncoming(parsed.Rows);
            if (incomingResult.IsFailure)
            {
                return Result.Failure<ImportSummary>(incomingResult.Error);
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();

            IReadOnlyList<Translation> existing = await _translationRepository.GetAllAsync(cancellationToken);

            TranslationDiffPlan plan = TranslationDiffService.ComputePlan(
                existing,
                incomingResult.Value,
                command.GameVersionId,
                now);

            if (!command.AllowMassRemoval && plan.RemovedFraction > _settings.MaxRemovedFractionWithoutOverride)
            {
                return Result.Failure<ImportSummary>(
                    ImportErrors.MassRemovalBlocked(
                        plan.Removed.Count,
                        plan.ComparableExistingCount,
                        plan.RemovedFraction,
                        _settings.MaxRemovedFractionWithoutOverride));
            }

            _translationRepository.InsertRange(plan.Added);

            foreach (TranslationSourceChange change in plan.SourceChanges)
            {
                change.Existing.ApplySourceChange(change.NewSource, command.GameVersionId, now);
            }

            foreach (Translation removed in plan.Removed)
            {
                removed.MarkRemoved(command.GameVersionId, now);
            }

            foreach (Translation restored in plan.Restored)
            {
                restored.Restore(now);
            }

            // Single SaveChanges = one transaction: the row changes and the version's processed
            // flag commit together, so IsProcessed flips only after the diff is durable (spec 0001).
            // Imports are admin-only and serial — concurrent imports of one version are out of scope,
            // so no optimistic-concurrency token is modelled.
            Result markProcessedResult = gameVersion.MarkProcessed();
            if (markProcessedResult.IsFailure)
            {
                return Result.Failure<ImportSummary>(markProcessedResult.Error);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return Result.Success(BuildSummary(plan));
        }

        private static Result<IReadOnlyList<IncomingTranslation>> MapToIncoming(IReadOnlyList<ParsedExportRow> rows)
        {
            List<IncomingTranslation> incoming = new(rows.Count);
            HashSet<FragmentKey> seen = [];

            foreach (ParsedExportRow row in rows)
            {
                Result<FragmentKey> keyResult = FragmentKey.Create(row.FileId, row.GossipId);
                if (keyResult.IsFailure)
                {
                    return Result.Failure<IReadOnlyList<IncomingTranslation>>(
                        ImportErrors.InvalidRow(row.FileId, row.GossipId, keyResult.Error.Message));
                }

                Result<TranslationSource> sourceResult = TranslationSource.Create(row.Content, row.ArgsOrder, row.ArgsId);
                if (sourceResult.IsFailure)
                {
                    return Result.Failure<IReadOnlyList<IncomingTranslation>>(
                        ImportErrors.InvalidRow(row.FileId, row.GossipId, sourceResult.Error.Message));
                }

                if (!seen.Add(keyResult.Value))
                {
                    return Result.Failure<IReadOnlyList<IncomingTranslation>>(
                        ImportErrors.DuplicateFragmentKey(row.FileId, row.GossipId));
                }

                incoming.Add(new IncomingTranslation(keyResult.Value, sourceResult.Value));
            }

            return Result.Success<IReadOnlyList<IncomingTranslation>>(incoming);
        }

        private static ImportSummary BuildSummary(TranslationDiffPlan plan)
        {
            List<string> warnings = [];
            if (plan.Restored.Count > 0)
            {
                warnings.Add($"{plan.Restored.Count} previously-removed row(s) re-added with an unchanged source and restored.");
            }

            return new ImportSummary(
                Added: plan.Added.Count,
                SourceChanged: plan.SourceChanges.Count,
                Invalidated: plan.InvalidatedCount,
                Removed: plan.Removed.Count,
                Unchanged: plan.UnchangedCount,
                Warnings: warnings);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("/api/v1/game-versions/{id:guid}/import", async (
                Guid id,
                IFormFile file,
                ICommandHandler<Command, Result<ImportSummary>> handler,
                CancellationToken cancellationToken,
                [FromQuery] bool allowMassRemoval = false) =>
            {
                await using Stream stream = file.OpenReadStream();

                Command command = new(GameVersionId.Create(id), stream, allowMassRemoval);

                Result<ImportSummary> result = await handler.Handle(command, cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(ImportExportedTexts))
            .WithTags("Import")
            .RequireAuthorization(AuthorizationPolicies.RequireAdminRole)
            .DisableAntiforgery()
            .Produces<ImportSummary>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
