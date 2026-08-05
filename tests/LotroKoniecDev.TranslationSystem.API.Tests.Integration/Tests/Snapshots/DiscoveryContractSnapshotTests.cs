using System.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// Pins the service document whole, for the two callers that bound the surface: the anonymous root
/// (#608) and an admin. The anonymous snapshot is the security-relevant one — the root is reachable
/// without credentials, so a rel leaking into it is a leaked affordance, and this file is what forces
/// that into a reviewable diff. The admin snapshot pins the complete rel vocabulary, so a rel silently
/// moving between tiers shows up as two diffs instead of none.
/// </summary>
/// <remarks>
/// <c>DiscoveryHateoasTests</c> still owns the behavior — which caller shape may see which rel is a
/// statement about many inputs, and it asserts the sets exactly. These snapshots answer the other
/// question: did anything at all about the document change (an href shape, a method, a property name).
/// The API is stateless here, so no seed is needed and the payload is byte-stable per caller.
/// </remarks>
[Collection("TranslationApi")]
public sealed class DiscoveryContractSnapshotTests
{
    private readonly TranslationSystemApiFactory _factory;

    public DiscoveryContractSnapshotTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Discovery_Anonymously_MatchesThePublicHateoasContract()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await ApiSnapshot.GetHateoasAsync(client, "/");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Verifier.Verify(body, "json");
    }

    [Fact]
    public async Task Discovery_AsAdmin_MatchesTheFullHateoasContract()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Admin));

        using HttpResponseMessage response = await ApiSnapshot.GetHateoasAsync(client, "/");
        string body = await ApiSnapshot.IndentAsync(response);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(MediaTypes.HateoasJson);
        await Verifier.Verify(body, "json");
    }
}
