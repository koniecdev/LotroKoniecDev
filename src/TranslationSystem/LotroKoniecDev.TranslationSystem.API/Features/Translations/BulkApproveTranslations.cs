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
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.Abstractions;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The many-row <see cref="ApproveTranslation"/> (#322). The selection is a snapshot, so rows that can
/// no longer be approved are skipped: one out-of-date row never fails the batch, and there is no 404
/// or 422 for a single row.
/// </summary>
internal sealed class BulkApproveTranslations : IEndpoint
{
    /// <summary>
    /// The translations list's largest page size: a checkbox selection never covers more than one page.
    /// </summary>
    internal const int MaxIds = 100;

    internal sealed record Command(IReadOnlyList<TranslationId> Ids)
        : ICommand<Result<BulkApproveTranslationsResponse>>;

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(command => command.Ids)
                .NotEmpty()
                .WithMessage("At least one translation id is required.");

            RuleFor(command => command.Ids)
                .Must(ids => ids.Count <= MaxIds)
                .WithMessage($"A bulk approve carries at most {MaxIds} translations.");

            RuleForEach(command => command.Ids)
                .NotEqual(TranslationId.Empty)
                .WithMessage("A translation id is required.");
        }
    }

    internal sealed class Handler : ICommandHandler<Command, Result<BulkApproveTranslationsResponse>>
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

        public async ValueTask<Result<BulkApproveTranslationsResponse>> Handle(Command command, CancellationToken cancellationToken)
        {
            ValidationResult validationResult = await _validator.ValidateAsync(command, cancellationToken);
            if (!validationResult.IsValid)
            {
                string message = string.Join("; ", validationResult.Errors.Select(failure => failure.ErrorMessage));
                return Result.Failure<BulkApproveTranslationsResponse>(new Error("Translations.Validation", message, TypeOfError.Validation));
            }

            // So Approved + Skipped == Requested holds even when an id is sent twice.
            List<TranslationId> distinctIds = command.Ids.Distinct().ToList();

            // If this fails nothing can be credited, so the whole batch fails, as the single approve does.
            Result<TranslatorId> provisionResult = await _translatorProvisioner.ProvisionCurrentAsync(cancellationToken);
            if (provisionResult.IsFailure)
            {
                return Result.Failure<BulkApproveTranslationsResponse>(provisionResult.Error);
            }

            IReadOnlyList<Translation> rows = await _translationRepository.GetByIdsAsync(distinctIds, cancellationToken);
            DateTimeOffset now = _timeProvider.GetUtcNow();

            int approved = 0;
            foreach (Translation row in rows)
            {
                // The same rule as the per-row `approve` link. Skipping an approved row keeps its
                // reviewer.
                if (row.Status is not (TranslationStatus.Draft or TranslationStatus.NeedsReview))
                {
                    continue;
                }

                // The domain may still refuse, for example a row soft-removed after the list was drawn.
                Result approveResult = row.Approve(provisionResult.Value, now);
                if (approveResult.IsSuccess)
                {
                    approved++;
                }
            }

            if (approved > 0)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                // Scheduled, not awaited (PERF-04, ADR-0021 §1).
                _rebuildScheduler.Schedule(SupportedLanguages.Polish);
            }

            int requested = distinctIds.Count;
            return Result.Success(new BulkApproveTranslationsResponse(requested, approved, requested - approved));
        }
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("/api/v1/translations/approve", async (
                BulkApproveTranslationsRequest request,
                ICommandHandler<Command, Result<BulkApproveTranslationsResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                // A missing or empty body leaves Ids null; validation then answers 400.
                IReadOnlyList<Guid> ids = request.Ids ?? [];
                Command command = new([.. ids.Select(TranslationId.FromValue)]);

                Result<BulkApproveTranslationsResponse> result = await handler.Handle(command, cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(BulkApproveTranslations))
            .WithTags("Translations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdminRole)
            .Produces<BulkApproveTranslationsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }
}
