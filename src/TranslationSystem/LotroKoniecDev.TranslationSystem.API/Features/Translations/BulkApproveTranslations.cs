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
/// Approves several translation rows in one admin action (#322), the collection counterpart of the
/// single <see cref="ApproveTranslation"/> slice: the reviewer selects rows on the list and this
/// publishes them together. Best-effort — the selection is a snapshot, so every requested row that is
/// <em>still</em> approvable (a non-removed <see cref="TranslationStatus.Draft"/> /
/// <see cref="TranslationStatus.NeedsReview"/> row) is approved and the rest are silently skipped; a
/// single stale row never fails the batch. All approvals are stamped in one <c>SaveChanges</c>, and a
/// single debounced artifact rebuild is scheduled after the commit (PERF-04, ADR-0021) — only when at
/// least one row was actually approved. Requires the admin (reviewer) policy. The response reports
/// how many were requested / approved / skipped; it never 404s or 422s on an individual row.
/// </summary>
internal sealed class BulkApproveTranslations : IEndpoint
{
    /// <summary>
    /// The most ids one request may carry — the translations list's max page size, since a checkbox
    /// selection can never span more than a single rendered page.
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

            // De-duplicate so the row lookup and the approved/skipped tally stay consistent
            // (Approved + Skipped == Requested), even if the client repeats an id.
            List<TranslationId> distinctIds = command.Ids.Distinct().ToList();

            // First-touch lazy provisioning (ADR-0004): resolve the reviewer's local TranslatorId once,
            // before stamping any row. A failure means the batch cannot be attributed, so it fails whole
            // — the same guard the single approve slice applies.
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
                // Only Draft/NeedsReview rows are approvable — mirrors the per-item `approve` affordance
                // (the FE offers a checkbox for exactly these). Anything else (Untranslated, or an
                // already-Approved row) is skipped, so an already-approved row keeps its original approver.
                if (row.Status is not (TranslationStatus.Draft or TranslationStatus.NeedsReview))
                {
                    continue;
                }

                // A Draft/NeedsReview row can still fail the domain guard (e.g. it was soft-removed after
                // the list was rendered): leave it untouched and count it as skipped — the guard is
                // authoritative, never bypassed by the batch.
                Result approveResult = row.Approve(provisionResult.Value, now);
                if (approveResult.IsSuccess)
                {
                    approved++;
                }
            }

            if (approved > 0)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                // At least one row (re)entered the distributed set, so the pre-built Polish artifact is
                // stale. Scheduled once, after the commit (PERF-04, ADR-0021 §1): the rebuild runs
                // debounced in the background, so the response returns now. Nothing approved ⇒ nothing to
                // publish ⇒ no rebuild.
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
                // A missing/empty body binds Ids to null; normalize to an empty list so validation
                // (not a NullReferenceException) turns it into a 400.
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
