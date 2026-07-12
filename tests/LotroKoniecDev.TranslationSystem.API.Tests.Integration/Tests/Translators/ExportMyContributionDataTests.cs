using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Translators;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translators;

[Collection("TranslationApi")]
public sealed class ExportMyContributionDataTests : IAsyncLifetime
{
    private const string ExportPath = "/api/v1/translators/me/data-export";
    private const int FileId = 620756992;
    private const string CallerDisplayName = "Frodo Baggins";
    private const string CallerEmail = "frodo@shire.me";
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private Guid _callerIdentity;
    private TranslatorId _callerId;
    private TranslatorId _otherId;

    public ExportMyContributionDataTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");

        _callerIdentity = Guid.NewGuid();

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator caller = Translator.Create(
            IdentityId.FromValue(_callerIdentity),
            DisplayName.Create(CallerDisplayName).Value,
            Email.Create(CallerEmail).Value,
            Now).Value;
        Translator other = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Samwise Gamgee").Value, email: null, Now).Value;
        dbContext.Translators.AddRange(caller, other);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _callerId = caller.Id;
        _otherId = other.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Export_WithoutToken_ShouldReturn401()
    {
        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(ExportPath);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Export_WithNoContributions_ShouldReturnTheProfileWithAnEmptySummary()
    {
        // Act
        TranslatorDataExportResponse export = await ExportAsync();

        // Assert — the seeded profile is returned; nothing is attributed yet.
        export.Profile.ShouldNotBeNull();
        export.Profile.TranslatorId.ShouldBe(_callerId);
        export.Profile.IdentityId.Value.ShouldBe(_callerIdentity);
        export.Profile.DisplayName.ShouldBe(CallerDisplayName);
        export.Profile.Email.ShouldBe(CallerEmail);
        export.Contributions.SubmittedTotal.ShouldBe(0);
        export.Contributions.ApprovedTotal.ShouldBe(0);
        export.Contributions.SubmittedRows.ShouldBeEmpty();
        export.Contributions.ApprovedRows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Export_ShouldSummarizeAndListOnlyTheCallersAttribution()
    {
        // Arrange — caller submitted a draft (1001) and an approved row (1002), approved another
        // translator's row (1003); rows 1004/1005 belong entirely to the other translator.
        await SeedAsync(
            SubmittedRow(1001, _callerId),
            ApprovedRow(1002, submittedBy: _callerId, approvedBy: _otherId),
            ApprovedRow(1003, submittedBy: _otherId, approvedBy: _callerId),
            SubmittedRow(1004, _otherId),
            ApprovedRow(1005, submittedBy: _otherId, approvedBy: _otherId));

        // Act
        TranslatorDataExportResponse export = await ExportAsync();

        // Assert
        export.Contributions.SubmittedTotal.ShouldBe(2);
        export.Contributions.SubmittedDraft.ShouldBe(1);
        export.Contributions.SubmittedApproved.ShouldBe(1);
        export.Contributions.SubmittedNeedsReview.ShouldBe(0);
        export.Contributions.ApprovedTotal.ShouldBe(1);
        export.Contributions.SubmittedRows.Select(row => row.GossipId).ShouldBe([1001L, 1002L]);
        export.Contributions.ApprovedRows.Select(row => row.GossipId).ShouldBe([1003L]);
        export.Contributions.SubmittedRows.ShouldAllBe(row => row.FileId == FileId);
    }

    [Fact]
    public async Task Export_ShouldCountAnInvalidatedSubmissionAsNeedsReview()
    {
        // Arrange — a game update reworded the source under the caller's translation.
        Translation invalidated = SubmittedRow(1001, _callerId);
        invalidated.ApplySourceChange(
            TranslationSource.Create("Reworded source", null, null).Value, _versionId, Now);
        await SeedAsync(invalidated);

        // Act
        TranslatorDataExportResponse export = await ExportAsync();

        // Assert
        export.Contributions.SubmittedTotal.ShouldBe(1);
        export.Contributions.SubmittedNeedsReview.ShouldBe(1);
        export.Contributions.SubmittedRows.Single().Status.ShouldBe(TranslationStatus.NeedsReview);
    }

    [Fact]
    public async Task Export_ShouldIncludeSoftRemovedRows()
    {
        // Arrange — the attribution stays the caller's personal data even after the row left the
        // game catalog (ADR-0032).
        Translation removed = SubmittedRow(1001, _callerId);
        removed.MarkRemoved(_versionId, Now);
        await SeedAsync(removed);

        // Act
        TranslatorDataExportResponse export = await ExportAsync();

        // Assert
        export.Contributions.SubmittedTotal.ShouldBe(1);
        export.Contributions.SubmittedRows.Single().GossipId.ShouldBe(1001L);
    }

    private async Task<TranslatorDataExportResponse> ExportAsync()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessToken(
                AuthConstants.Roles.Translator,
                subject: _callerIdentity,
                displayName: CallerDisplayName,
                email: CallerEmail));

        HttpResponseMessage response = await client.GetAsync(ExportPath);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TranslatorDataExportResponse>(JsonOptions))!;
    }

    private async Task SeedAsync(params Translation[] rows)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        dbContext.Translations.AddRange(rows);
        await dbContext.SaveChangesAsync();
    }

    private Translation SubmittedRow(int gossipId, TranslatorId submittedBy)
    {
        Translation row = CreateUntranslatedRow(gossipId);
        row.ProvideTranslation("Polski tekst", submittedBy, Now);
        return row;
    }

    private Translation ApprovedRow(int gossipId, TranslatorId submittedBy, TranslatorId approvedBy)
    {
        Translation row = CreateUntranslatedRow(gossipId);
        row.ProvideTranslation("Polski tekst", submittedBy, Now);
        row.Approve(approvedBy, Now);
        return row;
    }

    private Translation CreateUntranslatedRow(int gossipId)
        => Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("Source text", null, null).Value,
            _versionId,
            Now).Value;
}
