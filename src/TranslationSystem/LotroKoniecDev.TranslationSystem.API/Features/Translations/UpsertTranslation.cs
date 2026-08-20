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
/// Creates or updates the Polish text of an existing translation row (spec 0001, #100). The row comes
/// from the import with the English source only, and this slice adds the translator's Polish.
/// Whatever the status was, it becomes <see cref="TranslationStatus.Draft"/>, and the translator is
/// taken from the authenticated identity.
/// Editing an <see cref="TranslationStatus.Approved"/> row takes it out of the distributed set, so a
/// rebuild of the ready-made file is scheduled after the change commits (PERF-04, ADR-0021).
/// Retranslating a <see cref="TranslationStatus.NeedsReview"/> row keeps its
/// <c>PreviousSourceText</c> until someone approves it.
/// Placeholders are stored exactly as they are; warning about a wrong number of them is M3's job.
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

            // The upper limit comes from the DAT format and is not a matter of taste: above it the
            // patcher cannot write the row at all (#598). Refusing here turns a failure halfway through
            // a patch on someone else's machine into a validation message the translator sees while
            // editing.
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

        public Handler(
            IValidator<Command> validator,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork,
            ITranslatorProvisioner translatorProvisioner,
            IApplicationReadDbContext readDbContext,
            TimeProvider timeProvider,
            ITranslationFileRebuildScheduler rebuildScheduler)
        {
            _validator = validator;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
            _translatorProvisioner = translatorProvisioner;
            _readDbContext = readDbContext;
            _timeProvider = timeProvider;
            _rebuildScheduler = rebuildScheduler;
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

            // A soft-removed row was dropped from the game and from the distributed file. It is out of
            // translation work (spec 0001), so it cannot take new Polish.
            if (translation.IsRemoved)
            {
                return Result.Failure<TranslationDetailResponse>(DomainErrors.TranslationEntity.CannotEditRemoved);
            }

            // Create the caller's Translator row if it does not exist yet, and commit it before the
            // foreign key is written (ADR-0004), so the submitter is a TranslatorId that really exists.
            Result<TranslatorId> provisionResult = await _translatorProvisioner.ProvisionCurrentAsync(cancellationToken);
            if (provisionResult.IsFailure)
            {
                return Result.Failure<TranslationDetailResponse>(provisionResult.Error);
            }

            // Editing an approved row takes it out of the distributed set, because the status becomes
            // Draft, so the file has to be rebuilt. Editing a row in any other status changes nothing
            // there.
            bool wasApproved = translation.Status is TranslationStatus.Approved;

            translation.ProvideTranslation(command.TranslatedText, provisionResult.Value, _timeProvider.GetUtcNow());

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            if (wasApproved)
            {
                // We schedule the rebuild instead of waiting for it (PERF-04): it runs in the
                // background, the response returns now, and a client that disconnects cannot leave the
                // commit stranded.
                _rebuildScheduler.Schedule(SupportedLanguages.Polish);
            }

            // Read the committed row back through the read model, so the response carries the joined
            // display names of the submitter and the approver (ADR-0004), just like the get-one view.
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
