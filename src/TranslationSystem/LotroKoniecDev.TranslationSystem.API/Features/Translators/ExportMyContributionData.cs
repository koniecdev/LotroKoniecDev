using LotroKoniecDev.SharedKernel.Messaging;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Auth;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using LotroKoniecDev.TranslationSystem.API.Common;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.ReadDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using Microsoft.EntityFrameworkCore;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translators;

/// <summary>
/// The TMS half of the GDPR Art. 15 export (ADR-0032). No role is needed: access to your own data must
/// not depend on being a translator today. Soft-removed rows are included, because the credit is
/// personal data whether or not the row still ships. Unlike the auth half it asks for no password
/// (ADR-0052); the frontend fetches it only after the auth server accepted the password (#690).
/// </summary>
internal sealed partial class ExportMyContributionData : IEndpoint
{
    internal sealed record Query(IdentityId IdentityId) : IQuery<Result<TranslatorDataExportResponse>>;

    internal sealed partial class Handler : IQueryHandler<Query, Result<TranslatorDataExportResponse>>
    {
        private static readonly ContributionSummaryDto EmptySummary = new(
            SubmittedTotal: 0,
            SubmittedDraft: 0,
            SubmittedApproved: 0,
            SubmittedNeedsReview: 0,
            ApprovedTotal: 0,
            SubmittedRows: [],
            ApprovedRows: []);

        private readonly IApplicationReadDbContext _readDbContext;
        private readonly ILogger<Handler> _logger;

        public Handler(IApplicationReadDbContext readDbContext, ILogger<Handler> logger)
        {
            _readDbContext = readDbContext;
            _logger = logger;
        }

        public async ValueTask<Result<TranslatorDataExportResponse>> Handle(
            Query query, CancellationToken cancellationToken)
        {
            LogGdprContributionExportRequested(_logger, query.IdentityId.Value);

            TranslatorReadModel? translator = await _readDbContext.Translators
                .FirstOrDefaultAsync(t => t.IdentityId == query.IdentityId, cancellationToken);

            // Only when best-effort provisioning was skipped (claims with no usable name). Credit is
            // stored as a TranslatorId, so with no profile there is no credit either.
            if (translator is null)
            {
                return Result.Success(new TranslatorDataExportResponse(null, EmptySummary));
            }

            List<ContributionRowDto> submittedRows = await _readDbContext.Translations
                .Where(t => t.SubmittedById == translator.Id)
                .OrderBy(t => t.FileId)
                .ThenBy(t => t.GossipId)
                .Select(t => new ContributionRowDto(t.Id, t.FileId, t.GossipId, t.Status))
                .ToListAsync(cancellationToken);

            List<ContributionRowDto> approvedRows = await _readDbContext.Translations
                .Where(t => t.ApprovedById == translator.Id)
                .OrderBy(t => t.FileId)
                .ThenBy(t => t.GossipId)
                .Select(t => new ContributionRowDto(t.Id, t.FileId, t.GossipId, t.Status))
                .ToListAsync(cancellationToken);

            ContributionSummaryDto summary = new(
                SubmittedTotal: submittedRows.Count,
                SubmittedDraft: submittedRows.Count(row => row.Status is TranslationStatus.Draft),
                SubmittedApproved: submittedRows.Count(row => row.Status is TranslationStatus.Approved),
                SubmittedNeedsReview: submittedRows.Count(row => row.Status is TranslationStatus.NeedsReview),
                ApprovedTotal: approvedRows.Count,
                SubmittedRows: submittedRows,
                ApprovedRows: approvedRows);

            TranslatorProfileExportDto profile = new(
                translator.Id,
                translator.IdentityId,
                translator.DisplayName,
                translator.Email,
                translator.ProvisionedAt);

            LogGdprContributionExportCompleted(
                _logger, query.IdentityId.Value, submittedRows.Count, approvedRows.Count);

            return Result.Success(new TranslatorDataExportResponse(profile, summary));
        }

        [LoggerMessage(EventId = EventIds.GdprContributionExportRequested, Level = LogLevel.Information,
            Message = "GDPR contribution export requested for identity {IdentityId}")]
        private static partial void LogGdprContributionExportRequested(ILogger logger, Guid identityId);

        [LoggerMessage(EventId = EventIds.GdprContributionExportCompleted, Level = LogLevel.Information,
            Message = "GDPR contribution export completed for identity {IdentityId}: {SubmittedCount} submitted, {ApprovedCount} approved rows")]
        private static partial void LogGdprContributionExportCompleted(
            ILogger logger, Guid identityId, int submittedCount, int approvedCount);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapGet("/api/v1/translators/me/data-export", async (
                ICurrentUserAccessor currentUserAccessor,
                IQueryHandler<Query, Result<TranslatorDataExportResponse>> handler,
                CancellationToken cancellationToken) =>
            {
                ValueMaybe<IdentityId> maybeIdentityId = currentUserAccessor.MaybeIdentityId;
                if (maybeIdentityId.HasNoValue)
                {
                    return Results.Unauthorized();
                }

                Result<TranslatorDataExportResponse> result =
                    await handler.Handle(new Query(maybeIdentityId.Value), cancellationToken);

                return result.IsSuccess
                    ? Results.Ok(result.Value)
                    : Results.Problem(result.Error.ToProblemDetails());
            })
            .WithName(nameof(ExportMyContributionData))
            .WithTags("Translators")
            // Tightening this policy signs every logged-in user out: see Rels.ContributionDataExport.
            .RequireAuthorization(AuthorizationPolicies.RequireAuthenticatedUser)
            .Produces<TranslatorDataExportResponse>(StatusCodes.Status200OK);
    }
}
