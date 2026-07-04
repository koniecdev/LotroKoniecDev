using System.Net;
using LotroKoniecDev.TranslationSystem.Contracts.GameVersions;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.E2E.Tests.Clients;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using Shouldly;
using Xunit.Abstractions;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Tests;

/// <summary>
/// Proves the layer the in-process integration tests cannot reach: a JWT minted by the real auth-api
/// (OAuth2 password grant) is accepted by a separate tms-api process after it validates the signature
/// against the live JWKS endpoint, and that the first authenticated write lazily provisions the translator.
/// </summary>
public sealed class AuthFlowE2ETests : E2ETestBase
{
    private const int FileId = 620_756_992;

    private readonly ITestOutputHelper _output;

    public AuthFlowE2ETests(E2ETestFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public async Task RealAdminToken_FromAuthApi_IsAcceptedByTmsApi_AndProvisionsTranslatorOnFirstWrite()
    {
        try
        {
            string adminToken = await LoginAsAdminAsync();
            adminToken.ShouldNotBeNullOrWhiteSpace();

            TranslationSystemApiClient admin = CreateTmsClient(adminToken);

            GameVersionResponse version = await admin.RegisterGameVersionAsync("48.0");
            await admin.ImportAsync(version.Id.Value, $"{FileId}||1||English one||NULL||NULL||1");

            TranslationDetailResponse edited = await admin.UpsertAsync(FileId, gossipId: 1, translatedText: "Polski jeden");

            edited.Status.ShouldBe(TranslationStatus.Draft);
            edited.Submitter.ShouldNotBeNull("the first authenticated write must lazily provision a translator and stamp it");
            edited.Submitter.DisplayName.ShouldNotBeNullOrWhiteSpace();
        }
        catch (Exception)
        {
            _output.WriteLine(await Fixture.GetAuthApiLogsAsync());
            _output.WriteLine(await Fixture.GetTmsApiLogsAsync());
            throw;
        }
    }

    [Fact]
    public async Task RegisteredTranslator_RealToken_IsAcceptedByTmsApi()
    {
        try
        {
            RegisteredTranslator translator = await RegisterAndLoginTranslatorAsync();
            translator.AccessToken.ShouldNotBeNullOrWhiteSpace();

            TranslationSystemApiClient client = CreateTmsClient(translator.AccessToken);

            HttpResponseMessage response = await client.ListTranslationsRawAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        catch (Exception)
        {
            _output.WriteLine(await Fixture.GetAuthApiLogsAsync());
            _output.WriteLine(await Fixture.GetTmsApiLogsAsync());
            throw;
        }
    }

    // The stats endpoint stands in for "any protected endpoint": the translations LIST went
    // deliberately anonymous in #310 (public read-only landing page), which these two probes
    // originally targeted.
    [Fact]
    public async Task TmsApi_RejectsProtectedEndpoint_WithoutToken()
    {
        TranslationSystemApiClient anonymous = CreateTmsClient();

        HttpResponseMessage response = await anonymous.GetStatsRawAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TmsApi_RejectsProtectedEndpoint_WithMalformedToken()
    {
        TranslationSystemApiClient client = CreateTmsClient("not-a-real-jwt");

        HttpResponseMessage response = await client.GetStatsRawAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
