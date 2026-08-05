using System.Net.Http.Headers;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// Pins the full <c>application/problem+json</c> body for every status family the error contract can
/// produce (#571): the three domain failures <c>ErrorExtensions.ToProblemDetails</c> maps (400
/// Validation, 404 NotFound, 422 DataConflict) plus the two auth rejections, which share the media
/// type but carry no domain <c>errorCode</c>.
/// </summary>
/// <remarks>
/// <c>ProblemDetailsContractTests</c> asserts the individual fields that carry meaning; these
/// snapshots exist for the fields nobody thought to assert — a <c>type</c> URI silently changing, an
/// extension appearing or disappearing, <c>detail</c> starting to leak internals (most likely on the
/// 403, where the framework knows why the policy failed). A deliberate change re-accepts the verified
/// file in the same PR.
/// </remarks>
[Collection("TranslationApi")]
public sealed class ProblemDetailsSnapshotTests : IAsyncLifetime
{
    /// <summary>Fixed so the 404 request is byte-identical on every run, before scrubbing.</summary>
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
        // Authentication rejections are written by the framework, not by the domain error mapper —
        // the snapshot is what proves the two surfaces stay indistinguishable to a client.
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/game-versions");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task AdminEndpoint_AsTranslator_MatchesTheForbiddenProblemContract()
    {
        // The 403 body is where an authorization failure would leak the policy that rejected the
        // caller; pinning it whole is the only way that stays visible.
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
