using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// Pins the whole problem body for 400, 404, 422, 401 and 403 (#571). <c>ProblemDetailsContractTests</c>
/// checks the meaningful fields; these catch the rest: a quietly changed <c>type</c>, an extension that
/// comes or goes, or a <c>detail</c> that starts to reveal internals.
/// </summary>
[Collection("TranslationApi")]
public sealed class ProblemDetailsSnapshotTests : IAsyncLifetime
{
    /// <summary>Fixed, so the 404 request is the same on every run.</summary>
    private static readonly Guid UnknownTranslationId = new("11111111-2222-3333-4444-555555555555");

    private readonly TranslationSystemApiFactory _factory;

    public ProblemDetailsSnapshotTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"TranslationArtifacts\", translation.\"Translators\" CASCADE;");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertTranslation_WithBlankText_MatchesTheValidationProblemContract()
    {
        using HttpClient client = TranslatorClient();

        using HttpResponseMessage response = await client.PutAsJsonAsync(
            "/api/v1/translations", new UpsertTranslationRequest(620756992, 1, "   "));
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task RegisterGameVersion_WhenAlreadyTaken_MatchesTheDataConflictProblemContract()
    {
        using HttpClient client = AdminClient();
        (await client.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest("48.0")))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        using HttpResponseMessage response =
            await client.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest("48.0.0"));
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task GetTranslation_WithUnknownId_MatchesTheNotFoundProblemContract()
    {
        using HttpClient client = TranslatorClient();

        using HttpResponseMessage response =
            await client.GetAsync($"/api/v1/translations/{UnknownTranslationId}");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task ProtectedEndpoint_WithoutToken_MatchesTheUnauthorizedProblemContract()
    {
        // The framework writes this, not our error mapper; the snapshot proves clients see one shape.
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task AdminEndpoint_AsTranslator_MatchesTheForbiddenProblemContract()
    {
        // A 403 is where a leak of the rejecting policy would show.
        using HttpClient client = TranslatorClient();

        using HttpResponseMessage response =
            await client.PostAsJsonAsync("/api/v1/game-versions", new RegisterGameVersionRequest("48.0"));
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await Verifier.Verify(body, "json");
    }

    private HttpClient TranslatorClient() => ClientForRole(AuthConstants.Roles.Translator);

    private HttpClient AdminClient() => ClientForRole(AuthConstants.Roles.Admin);

    private HttpClient ClientForRole(string role)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role));
        return client;
    }
}
