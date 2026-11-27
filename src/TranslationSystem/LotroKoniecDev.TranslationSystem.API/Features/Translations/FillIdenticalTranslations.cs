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
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Saves the typed Polish in this row and in every live row with no Polish whose English and args are the
/// same, in one action (#1587, spec 0019). A row with Polish is never touched. This row is written only when
/// its text changes, so an approved row with the same text stays approved. Its own endpoint, not an option
/// on the save, because it changes more rows than its address names (ADR-0040 amendment).
/// </summary>
internal sealed partial class FillIdenticalTranslations : IEndpoint
{
    internal sealed record Command(TranslationId Id, string TranslatedText)
        : ICommand<Result<FillIdenticalTranslationsResponse>>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            // Every copy has the same English, so one check of the typed text covers all of them.
            RuleFor(command => command.TranslatedText)
                .MustBeAWritableTranslation();
        }
    }

    internal sealed partial class Handler : ICommandHandler<Command, Result<FillIdenticalTranslationsResponse>>
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

        public async ValueTask<Result<FillIdenticalTranslationsResponse>> Handle(
            Command command,
            CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure<FillIdenticalTranslationsResponse>(
                    new Error("Translations.Validation", message, TypeOfError.Validation));
            }

            Maybe<Translation> translationMaybe = await _translationRepository.GetByIdAsync(command.Id, cancellationToken);
            if (translationMaybe.HasNoValue)
            {
                return Result.Failure<FillIdenticalTranslationsResponse>(DomainErrors.TranslationEntity.NotFound(command.Id));
            }

            Translation translation = translationMaybe.Value;

            // Before any permit, so a refused fill spends nothing (ADR-0072 §4).
            Result writableResult = TranslationSaveBudgetGate.CheckWritable(translation, command.TranslatedText);
            if (writableResult.IsFailure)
            {
                return Result.Failure<FillIdenticalTranslationsResponse>(writableResult.Error);
            }

            Result<TranslatorId> provisionResult = await _translatorProvisioner.ProvisionCurrentAsync(cancellationToken);
            if (provisionResult.IsFailure)
            {
                return Result.Failure<FillIdenticalTranslationsResponse>(provisionResult.Error);
            }

            TranslatorId translatorId = provisionResult.Value;
            TranslationStatus previousStatus = translation.Status;
            DateTimeOffset now = WholeMicroseconds(_timeProvider.GetUtcNow());

            // The text as the save stores it (#1476), made once and shared by every row; an untouched field is
            // then unchanged.
            string storedText = translation.Source.RemoveTrailingLineBreaksFrom(command.TranslatedText);
            bool changed = !string.Equals(translation.TranslatedText, storedText, StringComparison.Ordinal);
            bool replacesPolish = changed && translation.TranslatedText is not null;

            // A copy that cannot fit is refused on two counts, before any row is loaded (ADR-0072 amendment).
            int emptyRowCount = await _translationRepository.CountEmptyRowsWithSourceAsync(
                translation.Source, translation.Id, cancellationToken);
            int saves = emptyRowCount + (changed ? 1 : 0);
            if (saves > 0)
            {
                Result roomResult = await _saveBudgetGate.CheckRoomAsync(
                    _logger, translatorId, saves, replacesPolish, now, cancellationToken);
                if (roomResult.IsFailure)
                {
                    return Result.Failure<FillIdenticalTranslationsResponse>(roomResult.Error);
                }
            }

            // Found again now, never sent by the client, so a row filled after the page load is left alone.
            IReadOnlyList<Translation> emptyRows = saves > 0
                ? await _translationRepository.GetEmptyRowsWithSourceAsync(translation.Source, translation.Id, cancellationToken)
                : [];

            if (changed || emptyRows.Count > 0)
            {
                Result saveResult = await SaveAsync(
                    translation, emptyRows, storedText, changed, replacesPolish, translatorId, now);
                if (saveResult.IsFailure)
                {
                    return Result.Failure<FillIdenticalTranslationsResponse>(saveResult.Error);
                }

                if (replacesPolish)
                {
                    _saveBudgetGate.LogOverwrite(
                        _logger, translatorId, translation.FragmentKey.FileId, translation.FragmentKey.GossipId, previousStatus);
                }

                // A copy is a draft of a row that was not in the file, so only this row can take one out.
                if (changed && previousStatus is TranslationStatus.Approved)
                {
                    _rebuildScheduler.Schedule(SupportedLanguages.Polish);
                }

                LogIdenticalRowsFilled(
                    _logger, translatorId.Value, translation.FragmentKey.FileId, translation.FragmentKey.GossipId,
                    emptyRows.Count, changed);
            }

            // Through the read model, so the response carries the joined display names (ADR-0004).
            TranslationDetailResponse? detail = await _readDbContext.Translations
                .Where(row => row.Id == translation.Id)
                .Select(TranslationProjections.ToDetail)
                .FirstOrDefaultAsync(cancellationToken);

            return detail is null
                ? Result.Failure<FillIdenticalTranslationsResponse>(DomainErrors.TranslationEntity.NotFound(command.Id))
                : Result.Success(new FillIdenticalTranslationsResponse(detail, emptyRows.Count, changed));
        }

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

        /// <summary>PostgreSQL keeps whole microseconds, so the stamp check can compare the saved time exactly.</summary>
        private static DateTimeOffset WholeMicroseconds(DateTimeOffset value) =>
            new(value.Ticks - value.Ticks % TimeSpan.TicksPerMicrosecond, value.Offset);

        /// <summary>
        /// The permits, this row and every copy are one action (ADR-0072 amendment of 2026-11-27). From the
        /// permits to the commit nothing listens to the request's token, so a client that leaves never stops a
        /// fill halfway, and any failure on the way hands every permit back.
        /// </summary>
        private async Task<Result> SaveAsync(
            Translation translation,
            IReadOnlyList<Translation> emptyRows,
            string translatedText,
            bool changed,
            bool replacesPolish,
            TranslatorId translatorId,
            DateTimeOffset now)
        {
            IReadOnlyList<TranslationSavePermit> permits =
                TranslationSaveBudgetGate.NewPermits(emptyRows.Count + (changed ? 1 : 0), replacesPolish, now);

            // From the first permit on, so a failure inside the permit step itself gives them back too.
            try
            {
                Result budgetResult = await _saveBudgetGate.SpendAsync(
                    _logger, translatorId, permits, now, CancellationToken.None);
                if (budgetResult.IsFailure)
                {
                    return budgetResult;
                }

                if (changed)
                {
                    translation.ProvideTranslation(translatedText, translatorId, now);
                }

                foreach (Translation emptyRow in emptyRows)
                {
                    emptyRow.ProvideTranslation(translatedText, translatorId, now);
                }

                // When a fault hides whether the commit landed, the stamp of one written row tells.
                TranslationId stampedRowId = changed ? translation.Id : emptyRows[0].Id;
                await _unitOfWork.SaveChangesInTransactionAsync(
                    verifyCancellationToken => WasSavedAsync(stampedRowId, translatorId, now, verifyCancellationToken),
                    CancellationToken.None);
            }
            catch
            {
                await _saveBudgetGate.HandBackAsync(_logger, translatorId, permits);
                throw;
            }

            return Result.Success();
        }

        private Task<bool> WasSavedAsync(
            TranslationId rowId,
            TranslatorId translatorId,
            DateTimeOffset now,
            CancellationToken cancellationToken) =>
            _readDbContext.Translations.AnyAsync(
                row => row.Id == rowId && row.SubmittedById == translatorId && row.UpdatedAt == now,
                cancellationToken);

        [LoggerMessage(EventId = EventIds.IdenticalRowsFilled, Level = LogLevel.Information,
            Message = TranslationSaveBudgetLogText.FilledTemplate)]
        private static partial void LogIdenticalRowsFilled(
            ILogger logger, Guid translatorId, int fileId, long gossipId, int filledCount, bool translationSaved);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("/api/v1/translations/{id:guid}/fill-identical", async (
                Guid id,
                FillIdenticalTranslationsRequest request,
                ICommandHandler<Command, Result<FillIdenticalTranslationsResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                Command command = new(TranslationId.FromValue(id), request.TranslatedText);

                Result<FillIdenticalTranslationsResponse> result = await handler.Handle(command, cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(FillIdenticalTranslations))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireTranslatorRole)
            .Produces<FillIdenticalTranslationsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
