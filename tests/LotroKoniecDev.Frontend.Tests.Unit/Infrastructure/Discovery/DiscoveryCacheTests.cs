using System.Security.Claims;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationRels = LotroKoniecDev.TranslationSystem.Contracts.Hateoas.Rels;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

/// <summary>
/// Both legs of the discovery cache over a real <see cref="HybridCache"/> and substituted clients. Each
/// leg runs the same poisoning guard: it must never cache an anonymous link set under an authenticated
/// key (that would strip every signed-in user of the affordances their session actually has — for a
/// full day), a degraded response must mark the session dead and fall back to the anonymous links, and
/// a genuine outage stays a ProblemDetails failure — never cached, never reclassified as "session
/// expired".
/// </summary>
public sealed class DiscoveryCacheTests
{
    private const string ExportHref = "auth/account/data-export";
    private const string ContributionExportHref = "api/v1/translators/me/data-export";
    private const string Subject = "user-sub-1";

    private readonly IAuthSystemClient _authClient = Substitute.For<IAuthSystemClient>();
    private readonly ITranslationSystemClient _translationClient = Substitute.For<ITranslationSystemClient>();
    private readonly IDeadSessionRegistry _deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenAuthenticatedAndRelPresent_CachesTheLinkSet()
    {
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AuthenticatedDiscovery()),
                ApiResult.Failure<AuthDiscoveryResponse>(Problem(503)));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<AuthDiscoveryResponse> first = await cache.GetAuthSystemDiscoveryAsync();
        // The second call must be served from the cache — the client's queued failure never surfaces.
        ApiResult<AuthDiscoveryResponse> second = await cache.GetAuthSystemDiscoveryAsync();

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Value.Links.ShouldContain(link => link.Rel == Rels.ExportAccountData);
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenAuthenticatedGetsAnonymousLinks_DegradesAndMarksTheSessionDead()
    {
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(AnonymousDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<AuthDiscoveryResponse> result = await cache.GetAuthSystemDiscoveryAsync();

        // Degrades to a successful anonymous link set instead of an error box…
        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldNotContain(link => link.Rel == Rels.ExportAccountData);
        // …and the sign-out is invisible in the return value — the .Received() is the only proof.
        await _deadSessionRegistry.Received(1).MarkDeadAsync(Subject, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_NeverCachesAnonymousLinksUnderTheAuthenticatedKey()
    {
        // First call: the bearer never reached the API → anonymous set. Second call: the API answers
        // correctly. If the degraded set had been cached under the "user" key, the second call would
        // still be missing the export rel — for a full day.
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AnonymousDiscovery()),
                ApiResult.Success(AnonymousDiscovery()),
                ApiResult.Success(AuthenticatedDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<AuthDiscoveryResponse> degraded = await cache.GetAuthSystemDiscoveryAsync();
        ApiResult<AuthDiscoveryResponse> recovered = await cache.GetAuthSystemDiscoveryAsync();

        degraded.Value.Links.ShouldNotContain(link => link.Rel == Rels.ExportAccountData);
        recovered.Value.Links.ShouldContain(link => link.Rel == Rels.ExportAccountData);
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenAnonymous_DoesNotRequireTheExportRel()
    {
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(AnonymousDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: false);

        ApiResult<AuthDiscoveryResponse> result = await cache.GetAuthSystemDiscoveryAsync();

        result.IsSuccess.ShouldBeTrue();
        // An anonymous link set under the anonymous key is the correct state — no forced sign-out.
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenTheApiFails_ReturnsTheProblemAndDoesNotCacheIt()
    {
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Failure<AuthDiscoveryResponse>(Problem(503)),
                ApiResult.Success(AuthenticatedDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<AuthDiscoveryResponse> outage = await cache.GetAuthSystemDiscoveryAsync();
        ApiResult<AuthDiscoveryResponse> recovered = await cache.GetAuthSystemDiscoveryAsync();

        // A genuine outage is a failure (never reclassified as "session expired")…
        outage.IsFailure.ShouldBeTrue();
        outage.ProblemDetails!.Status.ShouldBe(503);
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // …and is never persisted under the 1-day TTL: the next call retries the live endpoint.
        recovered.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenTheFallbackFetchAlsoFails_ReturnsTheProblem()
    {
        // Degraded set under the user key, then the API dies before the anonymous fallback fetch —
        // the sentinel must not escape as an exception; errors stay values.
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AnonymousDiscovery()),
                ApiResult.Failure<AuthDiscoveryResponse>(Problem(503)));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<AuthDiscoveryResponse> result = await cache.GetAuthSystemDiscoveryAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenAuthenticatedAndRelPresent_CachesTheLinkSet()
    {
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AuthenticatedTranslationDiscovery()),
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<TranslationDiscoveryResponse> first = await cache.GetTranslationSystemDiscoveryAsync();
        // The second call must be served from the cache — the client's queued failure never surfaces.
        ApiResult<TranslationDiscoveryResponse> second = await cache.GetTranslationSystemDiscoveryAsync();

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Value.Links.ShouldContain(link => link.Rel == TranslationRels.ContributionDataExport);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenAuthenticatedGetsAnonymousLinks_DegradesAndMarksTheSessionDead()
    {
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(AnonymousTranslationDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<TranslationDiscoveryResponse> result = await cache.GetTranslationSystemDiscoveryAsync();

        // Degrades to the anonymous link set — the public pages keep rendering on the way out…
        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldNotContain(link => link.Rel == TranslationRels.ContributionDataExport);
        result.Value.Links.ShouldContain(link => link.Rel == TranslationRels.Progress);
        // …and the sign-out is invisible in the return value — the .Received() is the only proof.
        await _deadSessionRegistry.Received(1).MarkDeadAsync(Subject, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_NeverCachesAnonymousLinksUnderTheAuthenticatedKey()
    {
        // First call: the bearer never reached the API → anonymous set. Second call: the API answers
        // correctly. If the degraded set had been cached under the "user" key, the second call would
        // still be missing the authenticated entry points — for a full day.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AnonymousTranslationDiscovery()),
                ApiResult.Success(AnonymousTranslationDiscovery()),
                ApiResult.Success(AuthenticatedTranslationDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<TranslationDiscoveryResponse> degraded = await cache.GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> recovered = await cache.GetTranslationSystemDiscoveryAsync();

        degraded.Value.Links.ShouldNotContain(link => link.Rel == TranslationRels.ContributionDataExport);
        recovered.Value.Links.ShouldContain(link => link.Rel == TranslationRels.ContributionDataExport);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenAnonymous_DoesNotRequireTheAuthenticatedRels()
    {
        // The TMS root is anonymous by design (#608): a guest legitimately gets only the public entry
        // points, so the guard must not read that as a broken bearer.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(AnonymousTranslationDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: false);

        ApiResult<TranslationDiscoveryResponse> result = await cache.GetTranslationSystemDiscoveryAsync();

        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldContain(link => link.Rel == TranslationRels.Progress);
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenTheApiFails_ReturnsTheProblemAndDoesNotCacheIt()
    {
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)),
                ApiResult.Success(AuthenticatedTranslationDiscovery()));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<TranslationDiscoveryResponse> outage = await cache.GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> recovered = await cache.GetTranslationSystemDiscoveryAsync();

        // A genuine outage is a failure (never reclassified as "session expired")…
        outage.IsFailure.ShouldBeTrue();
        outage.ProblemDetails!.Status.ShouldBe(503);
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // …and is never persisted under the 1-day TTL: the next call retries the live endpoint.
        recovered.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenTheFallbackFetchAlsoFails_ReturnsTheProblem()
    {
        // Degraded set under the user key, then the API dies before the anonymous fallback fetch —
        // the sentinel must not escape as an exception; errors stay values.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AnonymousTranslationDiscovery()),
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)));
        DiscoveryCache cache = CreateCache(authenticated: true);

        ApiResult<TranslationDiscoveryResponse> result = await cache.GetTranslationSystemDiscoveryAsync();

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(503);
    }

    private DiscoveryCache CreateCache(bool authenticated)
    {
        ServiceCollection services = new();
        services.AddHybridCache();
        HybridCache hybridCache = services.BuildServiceProvider().GetRequiredService<HybridCache>();

        DefaultHttpContext httpContext = new();
        if (authenticated)
        {
            httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", Subject)],
                authenticationType: "test"));
        }

        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        return new DiscoveryCache(hybridCache, _translationClient, _authClient, accessor, _deadSessionRegistry);
    }

    private static AuthDiscoveryResponse AuthenticatedDiscovery() =>
        new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto(ExportHref, Rels.ExportAccountData, "GET")]
        };

    private static AuthDiscoveryResponse AnonymousDiscovery() =>
        new("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("auth/register", Rels.Register, "POST")]
        };

    /// <summary>
    /// What the TMS root answers an authenticated caller: the public entry points plus at least
    /// <c>contribution-data-export</c>, whose endpoint requires nothing beyond authentication — the
    /// sentinel the guard reads.
    /// </summary>
    private static TranslationDiscoveryResponse AuthenticatedTranslationDiscovery() =>
        new("LotroKoniecDev.TranslationSystem")
        {
            Links =
            [
                new LinkDto("api/v1/progress", TranslationRels.Progress, "GET"),
                new LinkDto(ContributionExportHref, TranslationRels.ContributionDataExport, "GET")
            ]
        };

    /// <summary>What the TMS root answers a guest — the three anonymous endpoints, and nothing more.</summary>
    private static TranslationDiscoveryResponse AnonymousTranslationDiscovery() =>
        new("LotroKoniecDev.TranslationSystem")
        {
            Links =
            [
                new LinkDto("api/v1/progress", TranslationRels.Progress, "GET"),
                new LinkDto("api/v1/translations", TranslationRels.Translations, "GET")
            ]
        };

    private static ProblemDetails Problem(int status) => new()
    {
        Title = "Usługa chwilowo niedostępna",
        Status = status
    };
}
