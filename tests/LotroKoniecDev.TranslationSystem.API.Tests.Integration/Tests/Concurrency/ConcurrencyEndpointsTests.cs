using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Concurrency;

/// <summary>
/// Many identical writes at once from one user: upserting one fragment and approving one row. Both race
/// on creating the profile (ADR-0004), and approve floods the rebuild scheduler (PERF-04). None may give
/// a 500: with the xmin token at least one commits and the rest are clean 409s (AUDIT-EF-01;
/// TranslationConcurrencyTokenTests pins the token step by step).
/// </summary>
[Collection("TranslationApi")]
public sealed class ConcurrencyEndpointsTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string UpsertRoute = "/api/v1/translations";
    private const int ConcurrentRequests = 10;
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;

    public ConcurrencyEndpointsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);
        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ConcurrentUpsert_SameFragmentByOneNewIdentity_ShouldSerializeToWinnersAndProvisionExactlyOneTranslator()
    {
        // Arrange: one new identity fires every write, so the provisioning insert races with itself.
        await SeedUntranslatedRowAsync(gossipId: 1);
        using HttpClient client = TranslatorClient(Guid.NewGuid());

        // Act
        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, ConcurrentRequests)
            .Select(i => client.PutAsJsonAsync(UpsertRoute, new UpsertTranslationRequest(FileId, 1, $"Polski {i}")))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert: at least one commit, every loser a 409, and exactly one Translator.
        foreach (HttpResponseMessage response in responses)
        {
            ((int)response.StatusCode).ShouldBeLessThan(500, $"Unexpected server error: {response.StatusCode}");
        }

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBeGreaterThanOrEqualTo(1);
        responses
            .Where(response => response.StatusCode != HttpStatusCode.OK)
            .ShouldAllBe(response => response.StatusCode == HttpStatusCode.Conflict);
        (await GetStatusAsync(gossipId: 1)).ShouldBe(TranslationStatus.Draft);
        (await CountTranslatorsAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentApprove_SameDraftRow_ShouldSerializeToWinnersAndEndApproved()
    {
        // Arrange
        Guid id = await SeedDraftRowAsync(gossipId: 2, polish: "Witaj");
        using HttpClient client = AdminClient(Guid.NewGuid());

        // Act
        Task<HttpResponseMessage>[] tasks = Enumerable.Range(0, ConcurrentRequests)
            .Select(_ => client.PostAsync($"{UpsertRoute}/{id}/approve", null))
            .ToArray();
        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        // Assert: at least one approve, every loser a 409, and the approver provisioned once (the seeded
        // submitter is the only other Translator).
        foreach (HttpResponseMessage response in responses)
        {
            ((int)response.StatusCode).ShouldBeLessThan(500, $"Unexpected server error: {response.StatusCode}");
        }

        responses.Count(response => response.StatusCode == HttpStatusCode.NoContent).ShouldBeGreaterThanOrEqualTo(1);
        responses
            .Where(response => response.StatusCode != HttpStatusCode.NoContent)
            .ShouldAllBe(response => response.StatusCode == HttpStatusCode.Conflict);
        (await GetStatusAsync(gossipId: 2)).ShouldBe(TranslationStatus.Approved);
        (await CountTranslatorsAsync()).ShouldBe(2);
    }

    private async Task SeedUntranslatedRowAsync(int gossipId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("English", null, null).Value,
            _versionId,
            Now).Value;
        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedDraftRowAsync(int gossipId, string polish)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        // The submitter's foreign key target must exist (ADR-0004).
        Translator seeder = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(seeder);

        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create("English", null, null).Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation(polish, seeder.Id, Now);
        dbContext.Translations.Add(row);

        await dbContext.SaveChangesAsync();
        return row.Id.Value;
    }

    private async Task<TranslationStatus> GetStatusAsync(int gossipId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = await dbContext.Translations
            .AsNoTracking()
            .SingleAsync(translation => translation.FragmentKey.FileId == FileId && translation.FragmentKey.GossipId == gossipId);
        return row.Status;
    }

    private async Task<int> CountTranslatorsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translators.CountAsync();
    }

    private HttpClient TranslatorClient(Guid subject)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator, AuthConstants.Scopes.Api, subject));
        return client;
    }

    private HttpClient AdminClient(Guid subject)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin, AuthConstants.Scopes.Api, subject));
        return client;
    }
}
