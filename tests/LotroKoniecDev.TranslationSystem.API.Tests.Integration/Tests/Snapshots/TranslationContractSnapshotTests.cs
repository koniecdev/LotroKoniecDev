using System.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// Pins the whole shape of the translation read endpoints (#571): names, nesting, JSON types and every
/// link. A contract change fails here until the verified file is accepted in the same PR. The rules
/// about roles and state stay in <c>TranslationAggregateHateoasTests</c>.
/// </summary>
/// <remarks>
/// Three rows let one fixture cover a filled row, a row full of nulls, a middle page (the only place
/// <c>previous-page</c> and <c>next-page</c> both appear) and an empty response.
/// </remarks>
[Collection("TranslationApi")]
public sealed class TranslationContractSnapshotTests : IAsyncLifetime
{
    private const int FileId = 620756992;

    /// <summary>Shared by the seed and the wire-encoding assert, so they cannot drift.</summary>
    private const string SeededPolish = "Witaj w Śródziemiu!";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _translatorId;
    private Guid _draftRowId;

    public TranslationContractSnapshotTests(TranslationSystemApiFactory factory)
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
        _versionId = gameVersion.Id;

        Translator translator = Translator.Create(
            IdentityId.Create(), DisplayName.Create("Seed Author").Value, email: null, Now).Value;
        dbContext.Translators.Add(translator);
        _translatorId = translator.Id;

        // One filled draft and two untranslated rows with every nullable column empty.
        Translation draft = Untranslated(gossipId: 1, "Welcome to Middle-earth!");
        draft.ProvideTranslation(SeededPolish, _translatorId, Now);
        draft.ApplySourceChange(
            TranslationSource.Create("Welcome back to Middle-earth!", null, null).Value, _versionId, Now);
        _draftRowId = draft.Id.Value;

        dbContext.Translations.AddRange(
            draft,
            Untranslated(gossipId: 2, "The road goes ever on."),
            Untranslated(gossipId: 3, "Not all those who wander are lost."));
        await dbContext.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListTranslations_AsAdmin_MatchesTheHateoasContract()
    {
        using HttpClient client = AdminClient();

        using HttpResponseMessage response =
            await ApiSnapshot.GetHateoasAsync(client, "/api/v1/translations?page=1&pageSize=50");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.HateoasJson);
        await ApiSnapshot.ShouldServeUnescapedUtf8Async(response, SeededPolish);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task ListTranslations_Anonymously_MatchesThePublicHateoasContract()
    {
        // The public list (#309): items carry no links, the envelope keeps its pager.
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response =
            await ApiSnapshot.GetHateoasAsync(client, "/api/v1/translations?page=1&pageSize=50");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task ListTranslations_OnAMiddlePage_MatchesThePagedHateoasContract()
    {
        // One row per page puts page 2 in the middle, the only shape with both previous and next.
        using HttpClient client = AdminClient();

        using HttpResponseMessage response =
            await ApiSnapshot.GetHateoasAsync(client, "/api/v1/translations?page=2&pageSize=1");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task ListTranslations_WithNoMatches_MatchesTheEmptyEnvelopeContract()
    {
        // Clients must tell "no matches" from "broken", so items stay an empty array.
        using HttpClient client = AdminClient();

        using HttpResponseMessage response =
            await ApiSnapshot.GetHateoasAsync(client, "/api/v1/translations?search=Balrog");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task GetTranslation_AsAdmin_MatchesTheHateoasContract()
    {
        using HttpClient client = AdminClient();

        using HttpResponseMessage response =
            await ApiSnapshot.GetHateoasAsync(client, $"/api/v1/translations/{_draftRowId}");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ApiSnapshot.ShouldServeUnescapedUtf8Async(response, SeededPolish);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task ListTranslations_AsPlainJson_MatchesTheLinkLessContract()
    {
        // What a non-hypermedia client sees: links removed entirely, never left as empty arrays.
        using HttpClient client = AdminClient();

        using HttpResponseMessage response = await ApiSnapshot.GetAsync(
            client, "/api/v1/translations?page=1&pageSize=50", MediaTypes.Json);
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.Json);
        await Verifier.Verify(body, "json");
    }

    private Translation Untranslated(int gossipId, string source) =>
        Translation.CreateUntranslated(
            FragmentKey.Create(FileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;

    private HttpClient AdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin));
        return client;
    }
}
