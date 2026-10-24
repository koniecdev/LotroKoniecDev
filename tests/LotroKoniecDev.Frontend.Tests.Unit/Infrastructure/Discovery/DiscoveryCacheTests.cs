using System.Security.Claims;
using LotroKoniecDev.AuthSystem.Contracts.Hateoas;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.SharedKernel.Authorization;
using Microsoft.AspNetCore.Authentication;
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
/// Over a real <see cref="HybridCache"/> and substituted clients. Pins the cache keys per guest and per
/// account and roles (#842), the anonymous answer to a signed-in call (marks the session dead, cached
/// under neither key), and the outage that is never cached.
/// </summary>
public sealed class DiscoveryCacheTests
{
    private const string ExportHref = "auth/account/data-export";
    private const string ContributionExportHref = "api/v1/translators/me/data-export";
    private const string Subject = "user-sub-1";
    private const string AdminSubject = "admin-sub-1";
    private const string TranslatorSubject = "translator-sub-1";
    private const string RoleClaimType = "role";
    private const string AccessToken = "laptop-access-token";

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

        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldNotContain(link => link.Rel == Rels.ExportAccountData);
        // The sign-out is not in the return value, so .Received() is the only proof. It names the token,
        // not the user (#1061).
        await _deadSessionRegistry.Received(1).MarkDeadAsync(AccessToken, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The cookie validation ends a session with no usable token on its own (#1026).
    /// </summary>
    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenAuthenticatedWithoutATokenGetsAnonymousLinks_DoesNotMarkAnySession()
    {
        _authClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(AnonymousDiscovery()));
        DiscoveryCache cache = CreateCache(
            SignedIn(Subject, AuthConstants.Roles.Translator), CreateHybridCache(), accessToken: null);

        ApiResult<AuthDiscoveryResponse> result = await cache.GetAuthSystemDiscoveryAsync();

        result.IsSuccess.ShouldBeTrue();
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_NeverCachesAnonymousLinksUnderTheAuthenticatedKey()
    {
        // The first call gets the anonymous set. Had it been cached under the account's key, the second
        // call would miss the export rel for a day.
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

        // A real outage is a failure, never "session expired".
        outage.IsFailure.ShouldBeTrue();
        outage.ProblemDetails!.Status.ShouldBe(503);
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // It is never cached: the next call retries the live endpoint.
        recovered.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WhenAuthenticatedGetsAnonymousLinks_DoesNotCacheThemForGuests()
    {
        // A set from a call that carried a bearer is not stored under the anonymous key either, so the
        // guest's call reaches the API.
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

        // The public pages still render on the way out.
        result.IsSuccess.ShouldBeTrue();
        result.Value.Links.ShouldNotContain(link => link.Rel == TranslationRels.ContributionDataExport);
        result.Value.Links.ShouldContain(link => link.Rel == TranslationRels.Progress);
        // The sign-out is not in the return value, so .Received() is the only proof. It names the token,
        // not the user (#1061).
        await _deadSessionRegistry.Received(1).MarkDeadAsync(AccessToken, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The cookie validation ends a session with no usable token on its own (#1026).
    /// </summary>
    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenAuthenticatedWithoutATokenGetsAnonymousLinks_DoesNotMarkAnySession()
    {
        _translationClient.GetDiscoveryAsync(Arg.Any<CancellationToken>())
            .Returns(ApiResult.Success(AnonymousTranslationDiscovery()));
        DiscoveryCache cache = CreateCache(
            SignedIn(Subject, AuthConstants.Roles.Translator), CreateHybridCache(), accessToken: null);

        ApiResult<TranslationDiscoveryResponse> result = await cache.GetTranslationSystemDiscoveryAsync();

        result.IsSuccess.ShouldBeTrue();
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_NeverCachesAnonymousLinksUnderTheAuthenticatedKey()
    {
        // The first call gets the anonymous set. Had it been cached under the account's key, the second
        // call would miss the signed-in entry points for a day.
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
        // The TMS root is open to guests (#608), so their public set is not a broken bearer.
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

        // A real outage is a failure, never "session expired".
        outage.IsFailure.ShouldBeTrue();
        outage.ProblemDetails!.Status.ShouldBe(503);
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // It is never cached: the next call retries the live endpoint.
        recovered.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WhenAuthenticatedGetsAnonymousLinks_DoesNotCacheThemForGuests()
    {
        // A set from a call that carried a bearer is not stored under the anonymous key either, so the
        // guest's call reaches the API.
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
        // The API decides from the roles in the access token, so two accounts with the same role still
        // get separate entries.
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
        // A translator became an admin and signed in again, so the old answer must not be served.
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

    /// <param name="accessToken">Null leaves it out.</param>
    private DiscoveryCache CreateCache(ClaimsPrincipal user, HybridCache hybridCache, string? accessToken = AccessToken)
    {
        // HttpContext.GetTokenAsync reads the token from the ticket that IAuthenticationService returns.
        AuthenticationProperties properties = new();
        if (accessToken is not null)
        {
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = accessToken }]);
        }

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        authenticationService
            .AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(new AuthenticationTicket(user, properties, "Cookies")));

        ServiceCollection requestServices = new();
        requestServices.AddSingleton(authenticationService);

        DefaultHttpContext httpContext = new()
        {
            RequestServices = requestServices.BuildServiceProvider(),
            User = user
        };

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
    /// Carries <c>contribution-data-export</c>, the marker the check looks for.
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
