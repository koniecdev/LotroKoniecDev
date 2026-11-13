using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
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
using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Stores the translator's Polish on an imported row (spec 0001). It never creates a row: an unknown
/// fragment is a 404. Placeholders are stored as they are; the frontend editor checks them. A non-admin
/// spends the per-account save budget of ADR-0072 first.
/// </summary>
internal sealed partial class UpsertTranslation : IEndpoint
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

    internal sealed partial class Handler : ICommandHandler<Command, Result<TranslationDetailResponse>>
    {
        private readonly IValidator<Command> _validator;
        private readonly ITranslationRepository _translationRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ITranslatorProvisioner _translatorProvisioner;
        private readonly IApplicationReadDbContext _readDbContext;
        private readonly TimeProvider _timeProvider;
        private readonly ITranslationFileRebuildScheduler _rebuildScheduler;
        private readonly ICurrentUserAccessor _currentUserAccessor;
        private readonly ITranslationSaveLedger _saveLedger;
        private readonly IOptions<TranslationSaveBudgetSettings> _saveBudgetSettings;
        private readonly ILogger<Handler> _logger;

        public Handler(
            IValidator<Command> validator,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork,
            ITranslatorProvisioner translatorProvisioner,
            IApplicationReadDbContext readDbContext,
            TimeProvider timeProvider,
            ITranslationFileRebuildScheduler rebuildScheduler,
            ICurrentUserAccessor currentUserAccessor,
            ITranslationSaveLedger saveLedger,
            IOptions<TranslationSaveBudgetSettings> saveBudgetSettings,
            ILogger<Handler> logger)
        {
            _validator = validator;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
            _translatorProvisioner = translatorProvisioner;
            _readDbContext = readDbContext;
            _timeProvider = timeProvider;
            _rebuildScheduler = rebuildScheduler;
            _currentUserAccessor = currentUserAccessor;
            _saveLedger = saveLedger;
            _saveBudgetSettings = saveBudgetSettings;
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

            // A soft-removed row is out of translation work (spec 0001).
            if (translation.IsRemoved)
            {
                return Result.Failure<TranslationDetailResponse>(DomainErrors.TranslationEntity.CannotEditRemoved);
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
            bool isBudgeted = _currentUserAccessor.HasOnlyRegularUserPrivileges();
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (isBudgeted)
            {
                Result budgetResult = await SpendSaveBudgetAsync(translatorId, replacesPolish, now, cancellationToken);
                if (budgetResult.IsFailure)
                {
                    return Result.Failure<TranslationDetailResponse>(budgetResult.Error);
                }
            }

            translation.ProvideTranslation(command.TranslatedText, translatorId, now);

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // Only after the commit, so a refused or conflicting save never counts toward the owner's
            // total alert (ADR-0072).
            if (replacesPolish)
            {
                LogTranslationOverwritten(
                    _logger, translatorId.Value, command.FileId, command.GossipId, previousStatus, isBudgeted);
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

        private async Task<Result> SpendSaveBudgetAsync(
            TranslatorId translatorId,
            bool replacesPolish,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            TranslationSaveBudgetSettings settings = _saveBudgetSettings.Value;
            Guid saveId = Guid.CreateVersion7(now);

            TranslationSaveCounts counts = await _saveLedger.RecordAsync(
                saveId, translatorId, replacesPolish, now, TimeSpan.FromHours(settings.WindowHours), cancellationToken);

            TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(counts, replacesPolish, settings);

            switch (verdict.RefusedBy)
            {
                case TranslationSaveBudgetKind.Saves:
                    await _saveLedger.RemoveAsync(saveId, cancellationToken);
                    LogSaveBudget(
                        _logger, translatorId.Value, TranslationSaveBudgetKind.Saves, TranslationSaveBudgetOutcome.Refused,
                        counts.Saves, settings.SaveLimit, settings.WindowHours);
                    return Result.Failure(
                        TranslationSaveBudgetErrors.SaveBudgetExhausted(settings.SaveLimit, settings.WindowHours));

                case TranslationSaveBudgetKind.Overwrites:
                    await _saveLedger.RemoveAsync(saveId, cancellationToken);
                    LogSaveBudget(
                        _logger, translatorId.Value, TranslationSaveBudgetKind.Overwrites, TranslationSaveBudgetOutcome.Refused,
                        counts.Overwrites, settings.OverwriteLimit, settings.WindowHours);
                    return Result.Failure(
                        TranslationSaveBudgetErrors.OverwriteBudgetExhausted(settings.OverwriteLimit, settings.WindowHours));
            }

            // Every save at or above a threshold, not only the crossing one: two parallel saves can both
            // skip the exact number, and the alert looks back one hour.
            if (verdict.SavesAtAlertThreshold)
            {
                LogSaveBudget(
                    _logger, translatorId.Value, TranslationSaveBudgetKind.Saves, TranslationSaveBudgetOutcome.AlertThreshold,
                    counts.Saves, settings.SaveLimit, settings.WindowHours);
            }

            if (verdict.OverwritesAtAlertThreshold)
            {
                LogSaveBudget(
                    _logger, translatorId.Value, TranslationSaveBudgetKind.Overwrites, TranslationSaveBudgetOutcome.AlertThreshold,
                    counts.Overwrites, settings.OverwriteLimit, settings.WindowHours);
            }

            return Result.Success();
        }

        [LoggerMessage(EventId = EventIds.TranslationOverwritten, Level = LogLevel.Information,
            Message = TranslationSaveBudgetLogText.OverwrittenTemplate)]
        private static partial void LogTranslationOverwritten(
            ILogger logger, Guid translatorId, int fileId, long gossipId, TranslationStatus previousStatus, bool budgeted);

        [LoggerMessage(EventId = EventIds.TranslationSaveBudgetWarning, Level = LogLevel.Warning,
            Message = TranslationSaveBudgetLogText.BudgetTemplate)]
        private static partial void LogSaveBudget(
            ILogger logger,
            Guid translatorId,
            TranslationSaveBudgetKind budget,
            TranslationSaveBudgetOutcome outcome,
            int count,
            int limit,
            int windowHours);
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
