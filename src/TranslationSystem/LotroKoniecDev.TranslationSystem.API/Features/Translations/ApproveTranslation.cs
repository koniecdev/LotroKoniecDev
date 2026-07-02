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
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Repositories;
using LotroKoniecDev.TranslationSystem.Domain.Core.Errors;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// Approves a translation for distribution (spec 0001, #101): a reviewer flips the row to Approved,
/// which stamps the approving identity, clears any invalidation (<c>PreviousSourceText</c>) and pulls
/// the row back into the distributed set — so a debounced background rebuild of the pre-built Polish
/// artifact is scheduled after the change commits (PERF-04, ADR-0021); the response does not wait for
/// it. Requires the admin (reviewer) policy. A row with no Polish or a soft-removed row cannot be
/// approved (422); an unknown id is a 404.
/// </summary>
internal sealed class ApproveTranslation : IEndpoint
{
    internal sealed record Command(TranslationId Id) : ICommand<Result>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Id)
                .NotEqual(TranslationId.Empty)
                .WithMessage("The translation id is required.");
        }
    }

    internal sealed class Handler : ICommandHandler<Command, Result>
    {
        private readonly IValidator<Command> _validator;
        private readonly ITranslationRepository _translationRepository;
        private readonly IUnitOfWork _unitOfWork;
        private readonly ITranslatorProvisioner _translatorProvisioner;
        private readonly TimeProvider _timeProvider;
        private readonly ITranslationFileRebuildScheduler _rebuildScheduler;

        public Handler(
            IValidator<Command> validator,
            ITranslationRepository translationRepository,
            IUnitOfWork unitOfWork,
            ITranslatorProvisioner translatorProvisioner,
            TimeProvider timeProvider,
            ITranslationFileRebuildScheduler rebuildScheduler)
        {
            _validator = validator;
            _translationRepository = translationRepository;
            _unitOfWork = unitOfWork;
            _translatorProvisioner = translatorProvisioner;
            _timeProvider = timeProvider;
            _rebuildScheduler = rebuildScheduler;
        }

        public async ValueTask<Result> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure(new Error("Translations.Validation", message, TypeOfError.Validation));
            }

            Maybe<Translation> translationMaybe = await _translationRepository.GetByIdAsync(command.Id, cancellationToken);
            if (translationMaybe.HasNoValue)
            {
                return Result.Failure(DomainErrors.TranslationEntity.NotFound(command.Id));
            }

            Translation translation = translationMaybe.Value;

            // First-touch lazy provisioning (ADR-0004): get-or-create the reviewer's Translator row and
            // commit it before stamping the FK, so the approver is a valid local TranslatorId.
            Result<TranslatorId> provisionResult = await _translatorProvisioner.ProvisionCurrentAsync(cancellationToken);
            if (provisionResult.IsFailure)
            {
                return provisionResult;
            }

            Result approveResult = translation.Approve(provisionResult.Value, _timeProvider.GetUtcNow());
            if (approveResult.IsFailure)
            {
                return approveResult;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken);

            // The row has (re)entered the distributed set, so the pre-built Polish artifact is stale.
            // Scheduled, not awaited (PERF-04): the rebuild runs debounced in the background on the
            // host lifetime, so the response returns now and a disconnect cannot strand the commit.
            _rebuildScheduler.Schedule(SupportedLanguages.Polish);

            return Result.Success();
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("/api/v1/translations/{id:guid}/approve", async (
                Guid id,
                ICommandHandler<Command, Result> handler,
                CancellationToken cancellationToken) =>
            {
                Result result = await handler.Handle(new Command(TranslationId.FromValue(id)), cancellationToken);

                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(ApproveTranslation))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdminRole)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
