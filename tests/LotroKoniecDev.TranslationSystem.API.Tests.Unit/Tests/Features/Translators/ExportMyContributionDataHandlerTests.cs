using System.Collections.Generic;
using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.API.Features.Translators;
using LotroKoniecDev.TranslationSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.Logging.Abstractions;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translators;

public sealed class ExportMyContributionDataHandlerTests
{
    private const int FileId = 620756992;
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();

    private readonly List<TranslationReadModel> _translations = [];
    private readonly List<TranslatorReadModel> _translators = [];

    [Fact]
    public async Task Handle_WhenNoTranslatorProfileExists_ShouldReturnNullProfileWithAnEmptySummary()
    {
        // Arrange — the eager provisioning middleware is best-effort, so an identity can reach the
        // handler without a profile; the export must still succeed with a defined empty document.
        IdentityId unknownIdentity = IdentityId.Create();

        // Act
        TranslatorDataExportResponse export = await HandleAsync(unknownIdentity);

        // Assert
        export.Profile.ShouldBeNull();
        export.Contributions.SubmittedTotal.ShouldBe(0);
        export.Contributions.SubmittedDraft.ShouldBe(0);
        export.Contributions.SubmittedApproved.ShouldBe(0);
        export.Contributions.SubmittedNeedsReview.ShouldBe(0);
        export.Contributions.ApprovedTotal.ShouldBe(0);
        export.Contributions.SubmittedRows.ShouldBeEmpty();
        export.Contributions.ApprovedRows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_WithAttributedRows_ShouldSummarizeOnlyTheCallersAttribution()
    {
        // Arrange — caller submitted a draft, an approved and a needs-review row and approved one
        // foreign row; the other translator's own rows must not leak into the caller's export.
        IdentityId callerIdentity = IdentityId.Create();
        TranslatorId callerId = GivenTranslator(callerIdentity, "Frodo Baggins", "frodo@shire.me");
        TranslatorId otherId = GivenTranslator(IdentityId.Create(), "Samwise Gamgee", email: null);
        GivenRow(1001, TranslationStatus.Draft, submittedBy: callerId);
        GivenRow(1002, TranslationStatus.Approved, submittedBy: callerId, approvedBy: otherId);
        GivenRow(1003, TranslationStatus.NeedsReview, submittedBy: callerId);
        GivenRow(1004, TranslationStatus.Approved, submittedBy: otherId, approvedBy: callerId);
        GivenRow(1005, TranslationStatus.Approved, submittedBy: otherId, approvedBy: otherId);

        // Act
        TranslatorDataExportResponse export = await HandleAsync(callerIdentity);

        // Assert
        export.Profile.ShouldNotBeNull();
        export.Profile.TranslatorId.ShouldBe(callerId);
        export.Profile.IdentityId.ShouldBe(callerIdentity);
        export.Profile.DisplayName.ShouldBe("Frodo Baggins");
        export.Profile.Email.ShouldBe("frodo@shire.me");
        export.Contributions.SubmittedTotal.ShouldBe(3);
        export.Contributions.SubmittedDraft.ShouldBe(1);
        export.Contributions.SubmittedApproved.ShouldBe(1);
        export.Contributions.SubmittedNeedsReview.ShouldBe(1);
        export.Contributions.ApprovedTotal.ShouldBe(1);
        export.Contributions.SubmittedRows.Select(row => row.GossipId).ShouldBe([1001L, 1002L, 1003L]);
        export.Contributions.ApprovedRows.Select(row => row.GossipId).ShouldBe([1004L]);
    }

    [Fact]
    public async Task Handle_ShouldOrderRowsByFileIdThenGossipId()
    {
        // Arrange — rows land unsorted across two files; the export must order them for a stable,
        // diffable document.
        IdentityId callerIdentity = IdentityId.Create();
        TranslatorId callerId = GivenTranslator(callerIdentity, "Frodo Baggins", "frodo@shire.me");
        GivenRow(2000, TranslationStatus.Draft, submittedBy: callerId, fileId: FileId + 1);
        GivenRow(1002, TranslationStatus.Draft, submittedBy: callerId);
        GivenRow(1001, TranslationStatus.Draft, submittedBy: callerId);

        // Act
        TranslatorDataExportResponse export = await HandleAsync(callerIdentity);

        // Assert
        export.Contributions.SubmittedRows
            .Select(row => (row.FileId, row.GossipId))
            .ShouldBe([(FileId, 1001L), (FileId, 1002L), (FileId + 1, 2000L)]);
    }

    private async Task<TranslatorDataExportResponse> HandleAsync(IdentityId identityId)
    {
        ExportMyContributionData.Handler handler = new(
            new FakeReadDbContext(_translations, translators: _translators),
            NullLogger<ExportMyContributionData.Handler>.Instance);

        Result<TranslatorDataExportResponse> result =
            await handler.Handle(new ExportMyContributionData.Query(identityId), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        return result.Value;
    }

    private TranslatorId GivenTranslator(IdentityId identityId, string displayName, string? email)
    {
        TranslatorReadModel translator = new(TranslatorId.Create(), identityId, displayName, email, Now);
        _translators.Add(translator);
        return translator.Id;
    }

    private void GivenRow(
        int gossipId,
        TranslationStatus status,
        TranslatorId submittedBy,
        TranslatorId? approvedBy = null,
        int fileId = FileId)
        => _translations.Add(new TranslationReadModel(
            TranslationId.Create(),
            fileId,
            gossipId,
            $"source-{gossipId}",
            null,
            null,
            "Polski tekst",
            null,
            submittedBy,
            approvedBy,
            status,
            VersionId,
            null,
            null,
            Now,
            Now));
}
