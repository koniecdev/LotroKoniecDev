using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

/// <summary>
/// Builds an <see cref="IDiscoveryCache"/> whose TMS half offers a chosen set of links, for the loader
/// tests that now find their entry point by rel (#610).
/// <para>
/// The hrefs here deliberately do <b>not</b> look like the API's real routes. A loader test that checks
/// the request went to one of them proves the loader followed the href the server sent, and it could not
/// pass by accident if a hardcoded path came back.
/// </para>
/// </summary>
internal static class StubDiscoveryCache
{
    /// <summary>The prefix every stub href starts with. The API would never send anything like it.</summary>
    internal const string HrefPrefix = "/resolved-by-discovery/";

    /// <summary>The href the stub advertises for <paramref name="rel"/>.</summary>
    internal static string HrefFor(string rel) => HrefPrefix + rel;

    /// <summary>
    /// A cache whose TMS discovery sends one <c>GET</c> link per rel in <paramref name="rels"/>, each
    /// pointing at <see cref="HrefFor"/>. A rel that is not listed is simply missing, which is how a
    /// caller learns the server does not offer it.
    /// </summary>
    internal static IDiscoveryCache AdvertisingGet(params string[] rels) =>
        Advertising([.. rels.Select(rel => new LinkDto(HrefFor(rel), rel, HttpMethods.Get))]);

    /// <summary>A cache whose TMS discovery advertises exactly <paramref name="links"/>.</summary>
    internal static IDiscoveryCache Advertising(params LinkDto[] links)
    {
        IDiscoveryCache cache = Substitute.For<IDiscoveryCache>();
        cache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(
                new TranslationDiscoveryResponse("LotroKoniecDev.TranslationSystem") { Links = links }));
        return cache;
    }

    /// <summary>
    /// A cache whose TMS discovery is down. The loader has to pass this problem on as it is instead of
    /// guessing a URL: with no service document, no entry point is known.
    /// </summary>
    internal static IDiscoveryCache Unavailable(int status = StatusCodes.Status503ServiceUnavailable)
    {
        IDiscoveryCache cache = Substitute.For<IDiscoveryCache>();
        cache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Failure<TranslationDiscoveryResponse>(new ProblemDetails
            {
                Title = "Usługa chwilowo niedostępna",
                Status = status
            }));
        return cache;
    }
}
