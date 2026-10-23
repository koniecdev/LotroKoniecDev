using System.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// Pins the whole service document for the anonymous root (#608) and an admin. A rel that appears in the
/// anonymous file is an action given away, so the diff makes it visible; a rel moving between the two
/// shows as two diffs. Who may see which rel stays <c>DiscoveryHateoasTests</c>' job. Nothing is seeded.
/// </summary>
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
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe(MediaTypes.HateoasJson);
        await Verifier.Verify(body, "json");
    }
}
