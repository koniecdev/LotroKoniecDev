using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

/// <summary>
/// Builds an <see cref="IDiscoveryCache"/> whose TMS leg advertises a chosen link set, for the loader
/// tests that now resolve their entry point by rel (#610).
/// <para>
/// The hrefs handed out here deliberately do <b>not</b> look like the API's real routes: a loader test
/// that asserts the request landed on one of them is proving the loader followed the href the server
/// advertised, and could not pass by accident if a hardcoded path crept back in.
/// </para>
/// </summary>
internal static class StubDiscoveryCache
{
    /// <summary>The prefix every stub href carries — nothing the API would ever emit.</summary>
    internal const string HrefPrefix = "/resolved-by-discovery/";

    /// <summary>The href the stub advertises for <paramref name="rel"/>.</summary>
    internal static string HrefFor(string rel) => HrefPrefix + rel;

    /// <summary>
    /// A cache whose TMS discovery advertises a <c>GET</c> link per rel in <paramref name="rels"/>,
    /// each pointing at <see cref="HrefFor"/>. A rel not listed here is simply absent — which is how a
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
    /// A cache whose TMS discovery is down. The loader must surface this problem verbatim rather than
    /// guessing a URL — an unreachable service document means no entry point is known.
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
