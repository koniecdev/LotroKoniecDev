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
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Creates or updates the Polish content of an existing translation row (spec 0001, #100): the row
/// is born from import (English source only), this slice attaches the translator's Polish. Any prior
/// status moves to <see cref="TranslationStatus.Draft"/> and the submitting translator is stamped
/// from the authenticated identity. Editing an <see cref="TranslationStatus.Approved"/> row pulls it
/// out of the distributed set, so the pre-built artifact is regenerated after the change commits;
/// re-translating a <see cref="TranslationStatus.NeedsReview"/> row keeps its
/// <c>PreviousSourceText</c> until approve. Placeholders are stored verbatim — the
/// count-mismatch warning UX is M3.
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

            RuleFor(command => command.TranslatedText)
                .NotEmpty();
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
        private readonly IPrecomputedTranslationFileProjector _projector;

        public Handler(
            IValidator<Command> validator,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork,
            ITranslatorProvisioner translatorProvisioner,
            IApplicationReadDbContext readDbContext,
            TimeProvider timeProvider,
            IPrecomputedTranslationFileProjector projector)
        {
            _validator = validator;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
            _translatorProvisioner = translatorProvisioner;
            _readDbContext = readDbContext;
            _timeProvider = timeProvider;
            _projector = projector;
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

            // A soft-removed row was cut from the game and the distributed file — it is excluded from
            // translation work (spec 0001), so it cannot receive new Polish.
            if (translation.IsRemoved)
            {
                return Result.Failure<TranslationDetailResponse>(DomainErrors.TranslationEntity.CannotEditRemoved);
            }

            // First-touch lazy provisioning (ADR-0004): get-or-create the caller's Translator row and
            // commit it before stamping the FK, so the submitter is a valid local TranslatorId.
            Result<TranslatorId> provisionResult = await _translatorProvisioner.ProvisionCurrentAsync(cancellationToken);
            if (provisionResult.IsFailure)
            {
                return Result.Failure<TranslationDetailResponse>(provisionResult.Error);
            }

            // Editing an Approved row drops it from the distributed set (Status -> Draft), so the
            // artifact must be rebuilt; an edit to any other status does not change that set.
            bool wasApproved = translation.Status is TranslationStatus.Approved;

            translation.ProvideTranslation(command.TranslatedText, provisionResult.Value, _timeProvider.GetUtcNow());

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (wasApproved)
            {
                await _projector.RebuildAsync(SupportedLanguages.Polish, cancellationToken);
            }

            // Re-read the committed row through the read model so the response carries the joined
            // submitter / approver display names (ADR-0004), identical to the get-one view.
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
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
