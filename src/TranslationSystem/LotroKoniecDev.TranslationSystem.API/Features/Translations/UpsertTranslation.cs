using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Auth.Provisioning;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Stores the translator's Polish on an imported row (spec 0001). It never creates a row: an unknown
/// fragment is a 404. Polish with a different number of placeholders than the English is refused
/// (#1437). A plain translator (neither Admin nor Reviewer) spends the per-account save budget of
/// ADR-0072 first.
/// </summary>
internal sealed class UpsertTranslation : IEndpoint
{
    internal sealed record Command(int FileId, long GossipId, string TranslatedText)
        : ICommand<Result<TranslationDetailResponse>>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.FileId)
                .GreaterThan(0);

            RuleFor(command => command.GossipId)
                .GreaterThanOrEqualTo(0);

            // Above the DAT limit the patcher cannot write the row at all (#598, ADR-0043), so the
            // translator is told here, not a player halfway through a patch.
            RuleFor(command => command.TranslatedText)
                .NotEmpty()
                .MaximumLength(DatFormatConstants.MaxTranslatedTextLength);
        }
    }

    internal sealed class Handler : ICommandHandler<Command, Result<TranslationDetailResponse>>
    {
        private readonly IValidator<Command> _validator;
        private readonly ITranslationRepository _translationRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ITranslatorProvisioner _translatorProvisioner;
        private readonly IApplicationReadDbContext _readDbContext;
        private readonly TimeProvider _timeProvider;
        private readonly ITranslationFileRebuildScheduler _rebuildScheduler;
        private readonly TranslationSaveBudgetGate _saveBudgetGate;
        private readonly ILogger<Handler> _logger;

        public Handler(
            IValidator<Command> validator,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork,
            ITranslatorProvisioner translatorProvisioner,
            IApplicationReadDbContext readDbContext,
            TimeProvider timeProvider,
            ITranslationFileRebuildScheduler rebuildScheduler,
            TranslationSaveBudgetGate saveBudgetGate,
            ILogger<Handler> logger)
        {
            _validator = validator;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
            _translatorProvisioner = translatorProvisioner;
            _readDbContext = readDbContext;
            _timeProvider = timeProvider;
            _rebuildScheduler = rebuildScheduler;
            _saveBudgetGate = saveBudgetGate;
            _logger = logger;
        }

        public async ValueTask<Result<TranslationDetailResponse>> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure<TranslationDetailResponse>(new Error("Translations.Validation", message, TypeOfError.Validation));
            }

            Result<FragmentKey> keyResult = FragmentKey.Create(command.FileId, command.GossipId);
            if (keyResult.IsFailure)
            {
                return Result.Failure<TranslationDetailResponse>(keyResult.Error);
            }

            Maybe<Translation> translationMaybe = await _translationRepository.GetByFragmentKeyAsync(keyResult.Value, cancellationToken);
            if (translationMaybe.HasNoValue)
            {
                return Result.Failure<TranslationDetailResponse>(DomainErrors.TranslationEntity.NotFound(command.FileId, command.GossipId));
            }

            Translation translation = translationMaybe.Value;

            // Before the save permit, which commits on its own, so a refused save spends nothing (ADR-0072 §4).
            Result writableResult = TranslationSaveBudgetGate.CheckWritable(translation, command.TranslatedText);
            if (writableResult.IsFailure)
            {
                return Result.Failure<TranslationDetailResponse>(writableResult.Error);
            }

            // Commits the caller's Translator row before the foreign key is written (ADR-0004).
            Result<TranslatorId> provisionResult = await _translatorProvisioner.ProvisionCurrentAsync(cancellationToken);
            if (provisionResult.IsFailure)
            {
                return Result.Failure<TranslationDetailResponse>(provisionResult.Error);
            }

            TranslatorId translatorId = provisionResult.Value;
            TranslationStatus previousStatus = translation.Status;

            // Only an approved row leaves the distributed set, so only then is a rebuild needed.
            bool wasApproved = previousStatus is TranslationStatus.Approved;
            bool replacesPolish = translation.TranslatedText is not null;
            DateTimeOffset now = _timeProvider.GetUtcNow();

            Result<IReadOnlyList<Guid>> permitResult = await _saveBudgetGate.SpendAsync(
                _logger, translatorId, saves: 1, replacesPolish, now, cancellationToken);
            if (permitResult.IsFailure)
            {
                return Result.Failure<TranslationDetailResponse>(permitResult.Error);
            }

            translation.ProvideTranslation(command.TranslatedText, translatorId, now);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (replacesPolish)
            {
                _saveBudgetGate.LogOverwrite(_logger, translatorId, command.FileId, command.GossipId, previousStatus);
            }

            if (wasApproved)
            {
                // Scheduled, not awaited (PERF-04), so a client that disconnects cannot strand it.
                _rebuildScheduler.Schedule(SupportedLanguages.Polish);
            }

            // Through the read model, so the response carries the joined display names (ADR-0004).
            TranslationDetailResponse? response = await _readDbContext.Translations
                .Where(row => row.Id == translation.Id)
                .Select(TranslationProjections.ToDetail)
                .FirstOrDefaultAsync(cancellationToken);

            return response is null
                ? Result.Failure<TranslationDetailResponse>(DomainErrors.TranslationEntity.NotFound(command.FileId, command.GossipId))
                : Result.Success(response);
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPut("/api/v1/translations", async (
                UpsertTranslationRequest request,
                ICommandHandler<Command, Result<TranslationDetailResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(request.FileId, request.GossipId, request.TranslatedText);

                Result<TranslationDetailResponse> result = await handler.Handle(command, cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(UpsertTranslation))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<TranslationDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
