using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

/// <summary>
/// Three outcomes and no fourth: the server's href, the discovery outage passed on, or a 403. No path
/// builds a URL (#610, ADR-0041).
/// </summary>
public sealed class TranslationSystemEntryPointExtensionsTests
{
    [Fact]
    public async Task ResolveTranslationSystemHrefAsync_WhenAdvertised_ReturnsTheServersHref()
    {
        IDiscoveryCache cache = StubDiscoveryCache.AdvertisingGet(Rels.Progress, Rels.Translations);

        ApiResult<string> result = await cache.ResolveTranslationSystemHrefAsync(Rels.Progress);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(StubDiscoveryCache.HrefFor(Rels.Progress));
    }

    [Fact]
    public async Task ResolveTranslationSystemHrefAsync_WhenTheRelIsAbsent_ReturnsForbidden()
    {
        IDiscoveryCache cache = StubDiscoveryCache.AdvertisingGet(Rels.Progress);

        ApiResult<string> result = await cache.ResolveTranslationSystemHrefAsync(Rels.GameVersions);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status403Forbidden);
        result.ProblemDetails.Detail.ShouldNotBeNull();
        result.ProblemDetails.Detail!.ShouldContain(Rels.GameVersions);
    }

    [Fact]
    public async Task ResolveTranslationSystemHrefAsync_WhenTheServiceDocumentIsEmpty_ReturnsForbidden()
    {
        IDiscoveryCache cache = StubDiscoveryCache.Advertising();

        ApiResult<string> result = await cache.ResolveTranslationSystemHrefAsync(Rels.Translations);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ResolveTranslationSystemHrefAsync_WhenDiscoveryIsUnavailable_PassesThatProblemThrough()
    {
        // An outage must never read as a permissions message.
        IDiscoveryCache cache = StubDiscoveryCache.Unavailable();

        ApiResult<string> result = await cache.ResolveTranslationSystemHrefAsync(Rels.Progress);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task ResolveTranslationSystemHrefAsync_WhenTheRelIsANonGetAffordance_StillResolvesIt()
    {
        // A write action (the PUT upsert) resolves just as a GET does.
        IDiscoveryCache cache = StubDiscoveryCache.Advertising(
            new LinkDto("/advertised/translations", Rels.Translations, HttpMethods.Get),
            new LinkDto("/advertised/upsert", Rels.Upsert, HttpMethods.Put));

        ApiResult<string> result = await cache.ResolveTranslationSystemHrefAsync(Rels.Upsert);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("/advertised/upsert");
    }
}
