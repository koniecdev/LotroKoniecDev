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
/// Pins the <em>whole</em> shape of the translation read endpoints (#571) — every property name, the
/// nesting, the JSON types and the complete HATEOAS link set — where the behavioral suites assert only
/// the handful of fields each of them is about. An accidental contract change (a renamed property, a
/// dropped link, a nested object flattened) fails here until the verified file is re-accepted in the
/// same PR, which makes the break visible in review instead of silent.
/// </summary>
/// <remarks>
/// The seed is three rows on purpose, not one: it lets a single fixture cover the populated row, the
/// null-heavy untranslated row, a middle page (where <c>previous-page</c> / <c>next-page</c> are the
/// links most likely to regress unnoticed) and the empty envelope. Snapshots complement the behavioral
/// asserts, they do not replace them — <c>TranslationAggregateHateoasTests</c> still owns the
/// role-aware and state-aware link rules, because those are statements about behavior across many
/// inputs, not about the shape of one payload.
/// </remarks>
[Collection("TranslationApi")]
public sealed class TranslationContractSnapshotTests : IAsyncLifetime
{
    private const int FileId = 620756992;

    /// <summary>The seeded Polish, reused by the wire-encoding assert so seed and assert cannot drift.</summary>
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

        // A translated draft awaiting review (carries a submitter, Polish and the superseded English)
        // plus two untranslated rows (every nullable column empty), so one fixture covers the populated
        // and the null-heavy shape and still has enough rows for a middle page.
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
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);
        await ApiSnapshot.ShouldServeUnescapedUtf8Async(response, SeededPolish);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task ListTranslations_Anonymously_MatchesThePublicHateoasContract()
    {
        // The public read-only list (#309) is a different payload, not the same one minus a field:
        // items lose every action link while the envelope keeps its pagination navigation.
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
        // One row per page puts page 2 in the middle, which is the only shape where the envelope
        // carries previous-page AND next-page and both boolean flags are true.
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
        // An empty result is its own contract: clients have to distinguish "no matches" from "broken",
        // so items must stay an empty array and the counters must collapse coherently.
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
        // Plain application/json is the contract the CLI and any non-hypermedia client sees: the same
        // payload with the links arrays stripped entirely, never left as empty arrays.
        using HttpClient client = AdminClient();

        using HttpResponseMessage response = await ApiSnapshot.GetAsync(
            client, "/api/v1/translations?page=1&pageSize=50", MediaTypes.Json);
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.Json);
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
