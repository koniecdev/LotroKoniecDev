using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

/// <summary>
/// The seam every page now uses to get its first TMS URL (#610 / ADR-0041). Three outcomes and no
/// fourth: the advertised href, the discovery outage passed through verbatim, or a 403 saying the
/// server does not offer this caller that affordance. There is deliberately no path that composes a
/// URL locally, so a rel the server withheld can never turn into a request.
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
        // A rel the service document does not carry is an affordance this session does not have —
        // the answer is a failure, never a locally built path.
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
        // An outage is NOT a "you may not do this" — the caller must see the real cause, so a transient
        // 503 never renders as a permissions message.
        IDiscoveryCache cache = StubDiscoveryCache.Unavailable();

        ApiResult<string> result = await cache.ResolveTranslationSystemHrefAsync(Rels.Progress);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    [Fact]
    public async Task ResolveTranslationSystemHrefAsync_WhenTheRelIsANonGetAffordance_StillResolvesIt()
    {
        // The document carries one link per entry point; the rel is the whole lookup key. A write
        // affordance (PUT upsert) must resolve just as a GET does.
        IDiscoveryCache cache = StubDiscoveryCache.Advertising(
            new LinkDto("/advertised/translations", Rels.Translations, HttpMethods.Get),
            new LinkDto("/advertised/upsert", Rels.Upsert, HttpMethods.Put));

        ApiResult<string> result = await cache.ResolveTranslationSystemHrefAsync(Rels.Upsert);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("/advertised/upsert");
    }
}
