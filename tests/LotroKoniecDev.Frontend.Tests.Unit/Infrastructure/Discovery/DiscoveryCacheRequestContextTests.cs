using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.Discovery;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using AuthDiscoveryResponse = LotroKoniecDev.AuthSystem.Contracts.Discovery.DiscoveryResponse;
using AuthRels = LotroKoniecDev.AuthSystem.Contracts.Hateoas.Rels;
using TranslationDiscoveryResponse = LotroKoniecDev.TranslationSystem.Contracts.Discovery.DiscoveryResponse;
using TranslationRels = LotroKoniecDev.TranslationSystem.Contracts.Hateoas.Rels;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Discovery;

/// <summary>
/// #825: a cold discovery fetch carries the request's bearer and caller headers (ADR-0054), even when the
/// caller passes a token that can be cancelled, as the account export route does with RequestAborted.
/// The tests use the real client registrations and a real <see cref="HttpContextAccessor"/>, because a
/// substituted accessor returns its context on any thread and would hide a call made outside the request.
/// </summary>
public sealed class DiscoveryCacheRequestContextTests
{
    private const string AccessToken = "the-access-token";
    private const string VisitorAddress = "203.0.113.7";
    private const string AuthBaseUrl = "https://auth.lotro.test/";
    private const string TranslationSystemBaseUrl = "https://tms.lotro.test/";

    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string AuthCallerKey = new('k', 40);
    private static readonly string TranslationSystemCallerKey = new('t', 40);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDeadSessionRegistry _deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WithACancellableTokenOnAColdCache_SendsTheBearerAndTheCallerHeaders()
    {
        RecordingApi authApi = new(SignedInAuthLinks(), AnonymousAuthLinks());
        await using ServiceProvider provider = BuildProvider(authApi, new RecordingApi("{}", "{}"));
        EnterSignedInRequest();
        using CancellationTokenSource requestAborted = new();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDiscoveryCache>()
            .GetAuthSystemDiscoveryAsync(requestAborted.Token);

        authApi.Calls.ShouldBe([new RecordedCall("Bearer " + AccessToken, AuthCallerKey, VisitorAddress)]);
    }

    [Fact]
    public async Task GetTranslationSystemDiscoveryAsync_WithACancellableTokenOnAColdCache_SendsTheBearerAndTheCallerHeaders()
    {
        // The export route also resolves the 'contribution-data-export' rel through the TMS half.
        RecordingApi translationApi = new(SignedInTranslationLinks(), AnonymousTranslationLinks());
        await using ServiceProvider provider = BuildProvider(new RecordingApi("{}", "{}"), translationApi);
        EnterSignedInRequest();
        using CancellationTokenSource requestAborted = new();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDiscoveryCache>()
            .GetTranslationSystemDiscoveryAsync(requestAborted.Token);

        translationApi.Calls.ShouldBe(
            [new RecordedCall("Bearer " + AccessToken, TranslationSystemCallerKey, VisitorAddress)]);
    }

    [Fact]
    public async Task GetAuthSystemDiscoveryAsync_WithACancellableTokenOnAColdCache_KeepsTheUserSignedIn()
    {
        // The API answers like the real one: the signed-in set only when the bearer arrives. Without it
        // the cache would read the anonymous set as a dead token and sign a healthy user out.
        RecordingApi authApi = new(SignedInAuthLinks(), AnonymousAuthLinks());
        await using ServiceProvider provider = BuildProvider(authApi, new RecordingApi("{}", "{}"));
        EnterSignedInRequest();
        using CancellationTokenSource requestAborted = new();

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ApiResult<AuthDiscoveryResponse> result = await scope.ServiceProvider
            .GetRequiredService<IDiscoveryCache>()
            .GetAuthSystemDiscoveryAsync(requestAborted.Token);

        result.Value.Links.ShouldContain(link => link.Rel == AuthRels.ExportAccountData);
        // The sign-out does not show up in the return value, so this check is the only proof it did not happen.
        await _deadSessionRegistry.DidNotReceive().MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Sets the request's context the way ASP.NET Core does, through the accessor's async-local slot. It is
    /// not an async method on purpose: a value set inside an async method would not flow back to the test.
    /// </summary>
    private static void EnterSignedInRequest()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("sub", "user-sub-1")],
            authenticationType: "Cookies"));

        // HttpContext.GetTokenAsync reads the token from the ticket that IAuthenticationService returns.
        AuthenticationProperties properties = new();
        properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = AccessToken }]);
        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        authenticationService
            .AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, "Cookies")));

        ServiceCollection requestServices = new();
        requestServices.AddSingleton(authenticationService);

        DefaultHttpContext httpContext = new()
        {
            RequestServices = requestServices.BuildServiceProvider(),
            User = principal
        };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(VisitorAddress);

        new HttpContextAccessor().HttpContext = httpContext;
    }

    /// <summary>
    /// The frontend's own registrations with each socket handler swapped for a recording stub. A later
    /// ConfigurePrimaryHttpMessageHandler on the same named client wins, so the delegating handlers and
    /// the resilience pipeline stay exactly as Program.cs wires them.
    /// </summary>
    private ServiceProvider BuildProvider(RecordingApi authApi, RecordingApi translationApi)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(_deadSessionRegistry);
        services.AddSingleton<IOptions<AuthSystemSettings>>(Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = AuthBaseUrl,
            Authority = "https://auth.lotro.test",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"],
            CallerKey = AuthCallerKey
        }));
        services.AddSingleton<IOptions<TranslationSystemSettings>>(Microsoft.Extensions.Options.Options.Create(
            new TranslationSystemSettings { BaseUrl = TranslationSystemBaseUrl, CallerKey = TranslationSystemCallerKey }));
        services.AddHttpClients();
        services.AddDiscoveryCache();
        services.AddHttpClient<IAuthSystemClient, AuthSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => authApi);
        services.AddHttpClient<ITranslationSystemClient, TranslationSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => translationApi);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private static string SignedInAuthLinks() => JsonSerializer.Serialize(
        new AuthDiscoveryResponse("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("auth/account/data-export", AuthRels.ExportAccountData, "GET")]
        },
        JsonOptions);

    private static string AnonymousAuthLinks() => JsonSerializer.Serialize(
        new AuthDiscoveryResponse("LotroKoniecDev.AuthSystem")
        {
            Links = [new LinkDto("auth/register", AuthRels.Register, "POST")]
        },
        JsonOptions);

    private static string SignedInTranslationLinks() => JsonSerializer.Serialize(
        new TranslationDiscoveryResponse("LotroKoniecDev.TranslationSystem")
        {
            Links =
            [
                new LinkDto("api/v1/progress", TranslationRels.Progress, "GET"),
                new LinkDto("api/v1/translators/me/data-export", TranslationRels.ContributionDataExport, "GET")
            ]
        },
        JsonOptions);

    private static string AnonymousTranslationLinks() => JsonSerializer.Serialize(
        new TranslationDiscoveryResponse("LotroKoniecDev.TranslationSystem")
        {
            Links = [new LinkDto("api/v1/progress", TranslationRels.Progress, "GET")]
        },
        JsonOptions);

    /// <summary>A header sent more than once shows up as its values joined with a comma.</summary>
    private sealed record RecordedCall(string? Authorization, string? CallerKey, string? ClientAddress);

    /// <summary>
    /// Stands in for an API root. It copies the headers when the call is sent, because the client disposes
    /// the request message as soon as the response is read.
    /// </summary>
    private sealed class RecordingApi : HttpMessageHandler
    {
        private readonly string _signedInBody;
        private readonly string _anonymousBody;
        private readonly List<RecordedCall> _calls = [];

        public RecordingApi(string signedInBody, string anonymousBody)
        {
            _signedInBody = signedInBody;
            _anonymousBody = anonymousBody;
        }

        public IReadOnlyList<RecordedCall> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RecordedCall call = new(
                request.Headers.Authorization?.ToString(),
                HeaderValues(request, FrontendCallerHeaders.Key),
                HeaderValues(request, FrontendCallerHeaders.ClientAddress));
            lock (_calls)
            {
                _calls.Add(call);
            }

            string body = call.Authorization is null ? _anonymousBody : _signedInBody;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static string? HeaderValues(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? string.Join(",", values) : null;
    }
}
