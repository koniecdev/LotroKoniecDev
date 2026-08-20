using System.Net.Http.Headers;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Snapshots;

/// <summary>
/// Pins the whole service document for the two callers that mark its limits: the anonymous root (#608)
/// and an admin. The anonymous snapshot is the one that matters for security: the root can be read
/// without credentials, so a rel that appears there is an action we gave away, and this file forces that
/// into a diff a reviewer can see. The admin snapshot pins the full list of rels, so a rel that quietly
/// moves between the two shows up as two diffs instead of none.
/// </summary>
/// <remarks>
/// <c>DiscoveryHateoasTests</c> still owns the behaviour, which caller may see which rel, and it checks
/// the sets exactly. These snapshots answer a different question: did anything about the document change
/// at all, such as the shape of an href, a method or a property name.
/// The API holds no state here, so nothing has to be seeded and the payload is the same bytes every time
/// for a given caller.
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
