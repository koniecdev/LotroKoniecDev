using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

/// <summary>
/// Proves that a translator profile is created on first use and that creating it twice is safe (ADR-0004
/// and its 2026-06-24 amendment), against a real PostgreSQL. The caller's first authenticated request, a
/// plain read before any write, creates their <c>Translator</c>. Repeat requests from the same identity
/// add no second row, different identities get different rows, and the profile is refreshed from the
/// latest claims when they change.
/// </summary>
[Collection("TranslationApi")]
public sealed class TranslatorProvisioningTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const string Route = "/api/v1/translations";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;

    public TranslatorProvisioningTests(TranslationSystemApiFactory factory)
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
    public async Task RepeatedWritesBySameIdentity_ShouldProvisionExactlyOneTranslator()
    {
        // Arrange: two untranslated baseline rows; the same translator edits both, twice.
        await SeedUntranslatedAsync(gossipId: 1, source: "One");
        await SeedUntranslatedAsync(gossipId: 2, source: "Two");
        Guid subject = Guid.NewGuid();
        using HttpClient client = TranslatorClient(subject, "Legolas", "legolas@mirkwood.test");

        // Act: three writes by the same identity (the first provisions; the rest must not duplicate).
        await Upsert(client, gossipId: 1, "Jeden");
        await Upsert(client, gossipId: 2, "Dwa");
        await Upsert(client, gossipId: 1, "Jeden poprawione");

        // Assert: exactly one Translator row, keyed by the caller's identity and carrying its claims.
        List<Translator> translators = await LoadTranslatorsAsync();
        Translator translator = translators.ShouldHaveSingleItem();
        translator.IdentityId.Value.ShouldBe(subject);
        translator.DisplayName.Value.ShouldBe("Legolas");
        translator.Email.ShouldNotBeNull();
        translator.Email.Value.ShouldBe("legolas@mirkwood.test");
    }

    [Fact]
    public async Task WritesByDistinctIdentities_ShouldProvisionOneTranslatorEach()
    {
        // Arrange
        await SeedUntranslatedAsync(gossipId: 1, source: "One");
        await SeedUntranslatedAsync(gossipId: 2, source: "Two");
        using HttpClient first = TranslatorClient(Guid.NewGuid(), "Gimli", "gimli@erebor.test");
        using HttpClient second = TranslatorClient(Guid.NewGuid(), "Boromir", "boromir@gondor.test");

        // Act
        await Upsert(first, gossipId: 1, "Jeden");
        await Upsert(second, gossipId: 2, "Dwa");

        // Assert: two distinct identities, two rows.
        List<Translator> translators = await LoadTranslatorsAsync();
        translators.Count.ShouldBe(2);
        translators.Select(translator => translator.DisplayName.Value)
            .ShouldBe(["Gimli", "Boromir"], ignoreOrder: true);
    }

    [Fact]
    public async Task RenamedAccount_ShouldRefreshDisplayNameOnNextWrite_StillOneRow()
    {
        // Arrange: same identity (sub), a later token carrying a renamed display name and email.
        await SeedUntranslatedAsync(gossipId: 1, source: "One");
        await SeedUntranslatedAsync(gossipId: 2, source: "Two");
        Guid subject = Guid.NewGuid();

        // Act: first write provisions "Strider"; a later write carries the renamed "Aragorn".
        using (HttpClient before = TranslatorClient(subject, "Strider", "strider@rangers.test"))
        {
            await Upsert(before, gossipId: 1, "Jeden");
        }

        using (HttpClient after = TranslatorClient(subject, "Aragorn", "aragorn@gondor.test"))
        {
            await Upsert(after, gossipId: 2, "Dwa");
        }

        // Assert: still a single row, now converged on the latest claims.
        List<Translator> translators = await LoadTranslatorsAsync();
        Translator translator = translators.ShouldHaveSingleItem();
        translator.IdentityId.Value.ShouldBe(subject);
        translator.DisplayName.Value.ShouldBe("Aragorn");
        translator.Email.ShouldNotBeNull();
        translator.Email.Value.ShouldBe("aragorn@gondor.test");
    }

    [Fact]
    public async Task AuthenticatedReadWithoutAnyWrite_ShouldEagerlyProvisionTranslator()
    {
        // Arrange: a freshly registered + logged-in user who has not edited anything yet.
        Guid subject = Guid.NewGuid();
        using HttpClient client = TranslatorClient(subject, "Frodo", "frodo@shire.test");

        // Act: a plain authenticated read, not a write.
        HttpResponseMessage response = await client.GetAsync("/");

        // Assert: the read succeeds and the caller already has a Translator row from its claims
        // (ADR-0004 amendment): the "my profile" view works before any write.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        List<Translator> translators = await LoadTranslatorsAsync();
        Translator translator = translators.ShouldHaveSingleItem();
        translator.IdentityId.Value.ShouldBe(subject);
        translator.DisplayName.Value.ShouldBe("Frodo");
        translator.Email.ShouldNotBeNull();
        translator.Email.Value.ShouldBe("frodo@shire.test");
    }

    private async Task SeedUntranslatedAsync(int gossipId, string source)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;
        dbContext.Translations.Add(row);
        await dbContext.SaveChangesAsync();
    }

    private static async Task Upsert(HttpClient client, int gossipId, string polish)
    {
        HttpResponseMessage response = await client.PutAsJsonAsync(
            Route, new UpsertTranslationRequest(FileId, gossipId, polish));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<List<Translator>> LoadTranslatorsAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        return await dbContext.Translators.AsNoTracking().ToListAsync();
    }

    private HttpClient TranslatorClient(Guid subject, string displayName, string email)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessToken(
                AuthConstants.Roles.Translator, AuthConstants.Scopes.Api, subject, displayName, email));
        return client;
    }
}
