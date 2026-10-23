using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

/// <summary>
/// A discovery cache whose TMS half offers a chosen set of links (#610). The hrefs do not look like the
/// API's real routes, so a test cannot pass by accident if a hardcoded path came back.
/// </summary>
internal static class StubDiscoveryCache
{
    internal const string HrefPrefix = "/resolved-by-discovery/";

    internal static string HrefFor(string rel) => HrefPrefix + rel;

    internal static IDiscoveryCache AdvertisingGet(params string[] rels) =>
        Advertising([.. rels.Select(rel => new LinkDto(HrefFor(rel), rel, HttpMethods.Get))]);

    internal static IDiscoveryCache Advertising(params LinkDto[] links)
    {
        IDiscoveryCache cache = Substitute.For<IDiscoveryCache>();
        cache.GetTranslationSystemDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(
                new TranslationDiscoveryResponse("LotroKoniecDev.TranslationSystem") { Links = links }));
        return cache;
    }

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
