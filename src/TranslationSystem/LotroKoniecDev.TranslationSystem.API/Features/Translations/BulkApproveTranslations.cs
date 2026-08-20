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
/// Approves several translation rows in one admin action (#322). It is the many-row counterpart of
/// <see cref="ApproveTranslation"/>: the reviewer ticks rows on the list and this publishes them
/// together.
/// The selection is a snapshot, so this does what it can: every requested row that can still be
/// approved, meaning a <see cref="TranslationStatus.Draft"/> or
/// <see cref="TranslationStatus.NeedsReview"/> row that is not removed, is approved, and the rest are
/// skipped. One out-of-date row never fails the whole batch.
/// All approvals go in one <c>SaveChanges</c>, and one artifact rebuild is scheduled after the commit
/// (PERF-04, ADR-0021), but only when at least one row was really approved.
/// It needs the admin (reviewer) policy. The response says how many rows were requested, approved and
/// skipped, and it never returns 404 or 422 for an individual row.
/// </summary>
internal sealed class BulkApproveTranslations : IEndpoint
{
    /// <summary>
    /// The most ids one request may carry. It is the translations list's largest page size, because a
    /// selection of checkboxes can never cover more than one page.
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

            // Remove duplicates, so the row lookup and the approved and skipped counts still add up
            // (Approved + Skipped == Requested) even when the client sends the same id twice.
            List<TranslationId> distinctIds = command.Ids.Distinct().ToList();

            // Resolve the reviewer's local TranslatorId once, before any row is written (ADR-0004). If
            // that fails, nothing can be credited to anyone, so the whole batch fails, exactly as the
            // single approve does.
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
                // Only Draft and NeedsReview rows can be approved, the same rule the per-row `approve`
                // action uses, and the frontend offers a checkbox for exactly those. Anything else, an
                // Untranslated row or one that is already approved, is skipped, so an approved row keeps
                // the reviewer it already has.
                if (row.Status is not (TranslationStatus.Draft or TranslationStatus.NeedsReview))
                {
                    continue;
                }

                // A Draft or NeedsReview row can still be refused by the domain, for example because it
                // was soft-removed after the list was drawn. Leave it as it is and count it as skipped:
                // the domain decides, and the batch never goes around it.
                Result approveResult = row.Approve(provisionResult.Value, now);
                if (approveResult.IsSuccess)
                {
                    approved++;
                }
            }

            if (approved > 0)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                // At least one row is in the distributed set again, so the ready-made Polish file is out
                // of date. We schedule one rebuild after the commit (PERF-04, ADR-0021 §1) and do not
                // wait for it, so the response returns now. If nothing was approved there is nothing to
                // publish and no rebuild.
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
                // A missing or empty body leaves Ids null. Turn it into an empty list, so validation
                // returns a 400 instead of a NullReferenceException.
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
