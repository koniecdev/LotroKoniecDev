using FluentValidation;
using FluentValidation.Results;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Enums;
using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;

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
        private readonly ICurrentUserAccessor _currentUserAccessor;
        private readonly TimeProvider _timeProvider;
        private readonly ITranslationArtifactBuilder _artifactBuilder;

        public Handler(
            IValidator<Command> validator,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork,
            ICurrentUserAccessor currentUserAccessor,
            TimeProvider timeProvider,
            ITranslationArtifactBuilder artifactBuilder)
        {
            _validator = validator;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
            _currentUserAccessor = currentUserAccessor;
            _timeProvider = timeProvider;
            _artifactBuilder = artifactBuilder;
        }

        public async ValueTask<Result<TranslationDetailResponse>> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure<TranslationDetailResponse>(new Error("Translations.Validation", message, TypeOfError.Validation));
            }

            ValueMaybe<IdentityId> currentUser = _currentUserAccessor.MaybeIdentityId;
            if (currentUser.HasNoValue)
            {
                return Result.Failure<TranslationDetailResponse>(new Error(
                    "Translations.Unauthenticated",
                    "The current user identity is required to submit a translation.",
                    TypeOfError.Forbidden));
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

            // Editing an Approved row drops it from the distributed set (Status -> Draft), so the
            // artifact must be rebuilt; an edit to any other status does not change that set.
            bool wasApproved = translation.Status is TranslationStatus.Approved;

            translation.ProvideTranslation(command.TranslatedText, currentUser.Value, _timeProvider.GetUtcNow());

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (wasApproved)
            {
                await _artifactBuilder.RebuildAsync(SupportedLanguages.Polish, cancellationToken);
            }

            return Result.Success(ToDetailResponse(translation));
        }

        private static TranslationDetailResponse ToDetailResponse(Translation translation)
            => new(
                translation.Id,
                translation.FragmentKey.FileId,
                translation.FragmentKey.GossipId,
                translation.Source.Text,
                translation.Source.ArgsOrder,
                translation.Source.ArgsId,
                translation.TranslatedText,
                translation.PreviousSourceText,
                translation.SubmittedById?.Value,
                translation.Status,
                translation.CreatedAt,
                translation.UpdatedAt);
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
