using System.Security.Claims;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
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
/// Both halves of the discovery cache, over a real <see cref="HybridCache"/> and substituted clients.
/// Guests share one entry. A signed-in caller gets one for the account and its roles, because the API
/// sends each caller only the links that caller may follow (#842). An anonymous set of links is never
/// cached under a signed-in key, because that would take away, for a whole day, everything that user is
/// allowed to do. Such a response must mark the session dead and be served without being cached under
/// either key. A real outage stays a ProblemDetails failure, is never cached, and is never turned into
/// "session expired".
/// </summary>
public sealed class DiscoveryCacheTests
{
    private const string ExportHref = "auth/account/data-export";
    private const string ContributionExportHref = "api/v1/translators/me/data-export";
    private const string Subject = "user-sub-1";
    private const string AdminSubject = "admin-sub-1";
    private const string TranslatorSubject = "translator-sub-1";
    private const string RoleClaimType = "role";

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
        // The second call must come from the cache, so the client's queued failure never appears.
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
        // The sign-out does not show up in the return value, so the .Received() check is the only proof.
        await _deadSessionRegistry.Received(1).MarkDeadAsync(Subject, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_NeverCachesAnonymousLinksUnderTheAuthenticatedKey()
    {
        // First call: the token never reached the API, so we get the anonymous set. Second call: the API
        // answers properly. If the first, incomplete set had been cached under the account's key, the second
        // call would still be missing the export rel, for a whole day.
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
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
        // An anonymous set of links under the anonymous key is correct, so nobody is signed out.
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
    public async Task GetAuthSystemDiscoveryAsync_WhenAuthenticatedGetsAnonymousLinks_DoesNotCacheThemForGuests()
    {
        // The degraded set came from a call that carried a bearer, so it is not stored under the
        // anonymous key either. The guest's call must reach the API, so its queued failure appears.
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AnonymousDiscovery()),
                ApiResult.Failure<AuthDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();

        await CreateCache(authenticated: true, hybridCache).GetAuthSystemDiscoveryAsync();
        ApiResult<AuthDiscoveryResponse> guest =
            await CreateCache(authenticated: false, hybridCache).GetAuthSystemDiscoveryAsync();

        guest.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(503);
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
        // The second call must come from the cache, so the client's queued failure never appears.
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

        // It serves the anonymous set the API sent, so the public pages still render on the way out.
        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldNotContain(link => link.Rel == TranslationRels.ContributionDataExport);
        result.Value.Links.ShouldContain(link => link.Rel == TranslationRels.Progress);
        // The sign-out does not show up in the return value, so the .Received() check is the only proof.
        await _deadSessionRegistry.Received(1).MarkDeadAsync(Subject, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_NeverCachesAnonymousLinksUnderTheAuthenticatedKey()
    {
        // First call: the token never reached the API, so we get the anonymous set. Second call: the API
        // answers properly. If the first, incomplete set had been cached under the account's key, the second
        // call would still be missing the logged-in entry points, for a whole day.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
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
    public async Task GetTranslationSystemDiscoveryAsync_WhenAuthenticatedGetsAnonymousLinks_DoesNotCacheThemForGuests()
    {
        // The degraded set came from a call that carried a bearer, so it is not stored under the
        // anonymous key either. The guest's call must reach the API, so its queued failure appears.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AnonymousTranslationDiscovery()),
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();

        await CreateCache(authenticated: true, hybridCache).GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> guest =
            await CreateCache(authenticated: false, hybridCache).GetTranslationSystemDiscoveryAsync();

        guest.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(503);
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenTwoAccountsAreSignedIn_NeverShareAnAnswer()
    {
        // The second account must reach the API, so its queued failure appears.
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AuthenticatedDiscovery()),
                ApiResult.Failure<AuthDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();

        await CreateCache(SignedIn(Subject, AuthConstants.Roles.Translator), hybridCache)
            .GetAuthSystemDiscoveryAsync();
        ApiResult<AuthDiscoveryResponse> otherAccount =
            await CreateCache(SignedIn(TranslatorSubject, AuthConstants.Roles.Translator), hybridCache)
                .GetAuthSystemDiscoveryAsync();

        otherAccount.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(503);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetAuthSystemDiscoveryAsync_WhenTheSessionHasNoSubject_NeverCachesTheAnswer(string? subject)
    {
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AuthenticatedDiscovery()),
                ApiResult.Failure<AuthDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();

        ApiResult<AuthDiscoveryResponse> first =
            await CreateCache(SignedIn(subject, AuthConstants.Roles.Translator), hybridCache)
                .GetAuthSystemDiscoveryAsync();
        ApiResult<AuthDiscoveryResponse> second =
            await CreateCache(SignedIn(subject, AuthConstants.Roles.Translator), hybridCache)
                .GetAuthSystemDiscoveryAsync();

        first.IsSuccess.ShouldBeTrue();
        second.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(503);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenTwoGuestsCall_ServesTheSecondFromTheCache()
    {
        // Guests still share one entry, so the second guest never sees the client's queued failure.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AnonymousTranslationDiscovery()),
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();

        await CreateCache(authenticated: false, hybridCache).GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> secondGuest =
            await CreateCache(authenticated: false, hybridCache).GetTranslationSystemDiscoveryAsync();

        secondGuest.IsSuccess.ShouldBeTrue();
        secondGuest.Value.Links.ShouldContain(link => link.Rel == TranslationRels.Progress);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_AfterAnAdminCall_NeverServesTheAdminLinksToATranslator()
    {
        // The third answer is a failure, so the admin's second call proves it came from the cache.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AdminTranslationDiscovery()),
                ApiResult.Success(AuthenticatedTranslationDiscovery()),
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();
        DiscoveryCache adminCache = CreateCache(SignedIn(AdminSubject, AuthConstants.Roles.Admin), hybridCache);
        DiscoveryCache translatorCache =
            CreateCache(SignedIn(TranslatorSubject, AuthConstants.Roles.Translator), hybridCache);

        await adminCache.GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> translator = await translatorCache.GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> adminAgain = await adminCache.GetTranslationSystemDiscoveryAsync();

        translator.Value.Links.ShouldNotContain(link => link.Rel == TranslationRels.BulkApprove);
        translator.Value.Links.ShouldNotContain(link => link.Rel == TranslationRels.Register);
        adminAgain.Value.Links.ShouldContain(link => link.Rel == TranslationRels.BulkApprove);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_AfterATranslatorCall_NeverHidesTheAdminLinksFromAnAdmin()
    {
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AuthenticatedTranslationDiscovery()),
                ApiResult.Success(AdminTranslationDiscovery()));
        HybridCache hybridCache = CreateHybridCache();

        await CreateCache(SignedIn(TranslatorSubject, AuthConstants.Roles.Translator), hybridCache)
            .GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> admin =
            await CreateCache(SignedIn(AdminSubject, AuthConstants.Roles.Admin), hybridCache)
                .GetTranslationSystemDiscoveryAsync();

        admin.Value.Links.ShouldContain(link => link.Rel == TranslationRels.BulkApprove);
        admin.Value.Links.ShouldContain(link => link.Rel == TranslationRels.Register);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenTwoAccountsShareARoleInTheCookie_NeverShareAnAnswer()
    {
        // The API decides from the roles in the access token, so two cookies with the same role can still
        // get different answers. The second account must reach the API, so its queued failure appears.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AdminTranslationDiscovery()),
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();

        await CreateCache(SignedIn(Subject, AuthConstants.Roles.Translator), hybridCache)
            .GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> otherAccount =
            await CreateCache(SignedIn(TranslatorSubject, AuthConstants.Roles.Translator), hybridCache)
                .GetTranslationSystemDiscoveryAsync();

        otherAccount.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(503);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenTheAccountSignsInAgainWithNewRoles_GetsAFreshAnswer()
    {
        // The account was a translator, then became an admin and signed in again. The cookie now carries
        // the new role, so the old translator answer must not be served.
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AuthenticatedTranslationDiscovery()),
                ApiResult.Success(AdminTranslationDiscovery()));
        HybridCache hybridCache = CreateHybridCache();

        await CreateCache(SignedIn(Subject, AuthConstants.Roles.Translator), hybridCache)
            .GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> promoted =
            await CreateCache(SignedIn(Subject, AuthConstants.Roles.Admin), hybridCache)
                .GetTranslationSystemDiscoveryAsync();

        promoted.Value.Links.ShouldContain(link => link.Rel == TranslationRels.BulkApprove);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetTranslationSystemDiscoveryAsync_WhenTheSessionHasNoSubject_NeverCachesTheAnswer(
        string? subject)
    {
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(
                ApiResult.Success(AuthenticatedTranslationDiscovery()),
                ApiResult.Failure<TranslationDiscoveryResponse>(Problem(503)));
        HybridCache hybridCache = CreateHybridCache();

        ApiResult<TranslationDiscoveryResponse> first =
            await CreateCache(SignedIn(subject, AuthConstants.Roles.Translator), hybridCache)
                .GetTranslationSystemDiscoveryAsync();
        ApiResult<TranslationDiscoveryResponse> second =
            await CreateCache(SignedIn(subject, AuthConstants.Roles.Translator), hybridCache)
                .GetTranslationSystemDiscoveryAsync();

        first.IsSuccess.ShouldBeTrue();
        second.ProblemDetails.ShouldNotBeNull().Status.ShouldBe(503);
    }

    private DiscoveryCache CreateCache(bool authenticated, HybridCache? hybridCache = null) =>
        CreateCache(
            authenticated
                ? SignedIn(Subject, AuthConstants.Roles.Translator)
                : new ClaimsPrincipal(new ClaimsIdentity()),
            hybridCache ?? CreateHybridCache());

    private DiscoveryCache CreateCache(ClaimsPrincipal user, HybridCache hybridCache)
    {
        DefaultHttpContext httpContext = new() { User = user };

        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        return new DiscoveryCache(
            hybridCache,
            _translationClient,
            _authClient,
            accessor,
            _deadSessionRegistry);
    }

    private static ClaimsPrincipal SignedIn(string? subject, string role)
    {
        List<Claim> claims = [new Claim(RoleClaimType, role)];
        if (subject is not null)
        {
            claims.Add(new Claim("sub", subject));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test", "name", RoleClaimType));
    }

    private static HybridCache CreateHybridCache()
    {
        ServiceCollection services = new();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
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
    /// What the TMS root sends a logged-in caller: the public entry points plus at least
    /// <c>contribution-data-export</c>, whose endpoint needs nothing but a login. That is the marker the
    /// check looks for.
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

    /// <summary>
    /// What the TMS root sends an admin: the translator's set plus the two admin-only entry points.
    /// </summary>
    private static TranslationDiscoveryResponse AdminTranslationDiscovery() =>
        new("LotroKoniecDev.TranslationSystem")
        {
            Links =
            [
                new LinkDto("api/v1/progress", TranslationRels.Progress, "GET"),
                new LinkDto(ContributionExportHref, TranslationRels.ContributionDataExport, "GET"),
                new LinkDto("api/v1/translations/bulk-approve", TranslationRels.BulkApprove, "POST"),
                new LinkDto("api/v1/game-versions", TranslationRels.Register, "POST")
            ]
        };

    /// <summary>What the TMS root sends a guest: the three public endpoints and nothing else.</summary>
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
