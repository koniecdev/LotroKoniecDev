using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// #830: the frontend follows the links an API sends, and the handlers after this one add the bearer
/// token and the caller key to whatever address they get. So a link to another origin is refused before
/// anything is sent. The first tests drive the handler alone. The rest go through the real
/// <c>AddHttpClients</c> registration with a signed-in visitor: they fail when the guard is not wired,
/// when it sits inside the resilience handler, or when a client follows redirects.
/// </summary>
public sealed class SameOriginDelegatingHandlerTests
{
    // Built rather than written out, so no secret scanner mistakes test data for a key or a token.
    private static readonly string CallerKey = new('k', 40);
    private static readonly string AccessToken = new('a', 40);

    private const string AccessTokenName = "access_token";
    private const string VisitorAddress = "203.0.113.7";
    private const string AuthBaseUrl = "https://auth.lotro.test/";
    private const string TranslationSystemBaseUrl = "https://tms.lotro.test/";
    private const string OffOriginHref = "https://attacker.example/translations";

    // The circuit breaker needs ten calls in its window before it may open.
    private const int RefusalsPastTheBreakerThroughput = 12;

    [Theory]
    [InlineData("https://tms.lotro.test/translations")]
    [InlineData("https://TMS.Lotro.Test/translations")]
    [InlineData("https://tms.lotro.test:443/translations")]
    [InlineData("https://tms.lotro.test/translations?page=2#top")]
    public async Task SendAsync_ToTheConfiguredOrigin_PassesTheRequestOn(string href)
    {
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker();

        using HttpRequestMessage request = new(HttpMethod.Get, href);
        using HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.LastRequest.ShouldBeSameAs(request);
    }

    [Theory]
    [InlineData("https://attacker.example/translations")]
    [InlineData("https://api.tms.lotro.test/translations")]
    [InlineData("https://tms.lotro.test.attacker.example/translations")]
    [InlineData("https://tms.lotro.test@attacker.example/translations")]
    [InlineData("https://tms.lotro.test:8443/translations")]
    [InlineData("http://tms.lotro.test/translations")]
    [InlineData("http://tms.lotro.test:443/translations")]
    [InlineData("https://tms.lotro.test./translations")]
    public async Task SendAsync_ToAnotherOrigin_ThrowsAndSendsNothing(string href)
    {
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker();

        using HttpRequestMessage request = new(HttpMethod.Get, href);

        await Should.ThrowAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));
        inner.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_WithARelativeAddress_ThrowsAndSendsNothing()
    {
        // HttpClient joins a relative address to its base address before the handlers run, so a relative
        // one here means nobody did. With no origin to check, the request is refused.
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker();

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri("translations", UriKind.Relative));

        await Should.ThrowAsync<HttpRequestException>(() => invoker.SendAsync(request, CancellationToken.None));
        inner.LastRequest.ShouldBeNull();
    }

    [Theory]
    [InlineData(OffOriginHref)]
    [InlineData("//attacker.example/translations")]
    [InlineData("http://tms.lotro.test/translations")]
    public async Task TranslationSystemClient_ThroughTheRealPipeline_RefusesAnOffOriginLinkAndSendsNothing(string href)
    {
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildProvider(primary);
        ITranslationSystemClient client = provider.GetRequiredService<ITranslationSystemClient>();

        ApiResult<object> result = await client.GetApiResultAsync<object>(href);

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        primary.SendCount.ShouldBe(0);
    }

    [Fact]
    public async Task AuthSystemClient_ThroughTheRealPipeline_RefusesAnOffOriginLinkAndSendsNothing()
    {
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildProvider(primary);
        IAuthSystemClient client = provider.GetRequiredService<IAuthSystemClient>();

        ApiResult result = await client.PostApiResultAsync("https://attacker.example/auth/account/email", new { });

        result.IsFailure.ShouldBeTrue();
        result.ProblemDetails!.Status.ShouldBe(StatusCodes.Status503ServiceUnavailable);
        primary.SendCount.ShouldBe(0);
    }

    [Fact]
    public async Task TranslationSystemClient_ThroughTheRealPipeline_SendsTheTokenAndTheKeyToAnAbsoluteLinkOnItsOwnOrigin()
    {
        // The API's links are absolute (LinkGenerator.GetUriByName), so this is the everyday case. It also
        // proves the refusal above is not only a signed-out visitor sending nothing.
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildProvider(primary);
        ITranslationSystemClient client = provider.GetRequiredService<ITranslationSystemClient>();

        ApiResult<object> result = await client.GetApiResultAsync<object>(TranslationSystemBaseUrl + "translations");

        result.IsSuccess.ShouldBeTrue();
        primary.LastAuthorization.ShouldBe(new AuthenticationHeaderValue("Bearer", AccessToken));
        primary.LastCallerKey.ShouldBe(CallerKey);
    }

    [Fact]
    public async Task AuthSystemClient_ThroughTheRealPipeline_SendsTheTokenAndTheKeyToAnAbsoluteLinkOnItsOwnOrigin()
    {
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildProvider(primary);
        IAuthSystemClient client = provider.GetRequiredService<IAuthSystemClient>();

        ApiResult result = await client.PostApiResultAsync(AuthBaseUrl + "auth/account/email", new { });

        result.IsSuccess.ShouldBeTrue();
        primary.LastAuthorization.ShouldBe(new AuthenticationHeaderValue("Bearer", AccessToken));
        primary.LastCallerKey.ShouldBe(CallerKey);
    }

    [Fact]
    public async Task TranslationSystemClient_AfterManyRefusedLinks_StillReachesItsOwnOrigin()
    {
        // A guard inside the resilience handler would retry each refusal and count it as a failure, so
        // the circuit breaker would open and stop every TMS call for every visitor for 30 seconds.
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildProvider(primary);
        ITranslationSystemClient client = provider.GetRequiredService<ITranslationSystemClient>();
        for (int attempt = 0; attempt < RefusalsPastTheBreakerThroughput; attempt++)
        {
            await client.GetApiResultAsync<object>(OffOriginHref);
        }

        ApiResult<object> result = await client.GetApiResultAsync<object>(TranslationSystemBaseUrl + "translations");

        result.IsSuccess.ShouldBeTrue();
        primary.SendCount.ShouldBe(1);
    }

    [Fact]
    public async Task AuthSystemClient_AfterManyRefusedLinks_StillReachesItsOwnOrigin()
    {
        RecordingHttpMessageHandler primary = new();
        await using ServiceProvider provider = BuildProvider(primary);
        IAuthSystemClient client = provider.GetRequiredService<IAuthSystemClient>();
        for (int attempt = 0; attempt < RefusalsPastTheBreakerThroughput; attempt++)
        {
            await client.GetApiResultAsync<object>("https://attacker.example/auth/account/data-export");
        }

        ApiResult<object> result = await client.GetApiResultAsync<object>(AuthBaseUrl + "auth/account/data-export");

        result.IsSuccess.ShouldBeTrue();
        primary.SendCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(nameof(ITranslationSystemClient))]
    [InlineData(nameof(IAuthSystemClient))]
    public void TypedClient_ThroughTheRealRegistration_DoesNotFollowRedirects(string clientName)
    {
        // The guard sees only the first address. A redirect the socket handler followed on its own would
        // take the caller key to the new host, and only the bearer token is dropped on the way.
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(SignedInVisitor());
        AddSettings(services);
        services.AddHttpClients();
        using ServiceProvider provider = services.BuildServiceProvider();

        HttpMessageHandler handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegatingHandler)
        {
            handler = delegatingHandler.InnerHandler!;
        }

        handler.ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    private static (HttpMessageInvoker Invoker, StubHttpMessageHandler Inner) CreateInvoker()
    {
        StubHttpMessageHandler inner = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        SameOriginDelegatingHandler handler = new(
            new Uri(TranslationSystemBaseUrl),
            NullLogger<SameOriginDelegatingHandler>.Instance)
        {
            InnerHandler = inner
        };

        return (new HttpMessageInvoker(handler), inner);
    }

    /// <summary>
    /// The frontend's own registration with the socket handler swapped for a recording stub, the way
    /// FrontendCallerDelegatingHandlerTests does it, and a signed-in visitor who holds a token.
    /// </summary>
    private static ServiceProvider BuildProvider(HttpMessageHandler primary)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(SignedInVisitor());
        AddSettings(services);
        services.AddHttpClients();
        services.AddHttpClient<IAuthSystemClient, AuthSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);
        services.AddHttpClient<ITranslationSystemClient, TranslationSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);

        return services.BuildServiceProvider();
    }

    private static void AddSettings(ServiceCollection services)
    {
        services.AddSingleton<IOptions<AuthSystemSettings>>(Microsoft.Extensions.Options.Options.Create(new AuthSystemSettings
        {
            BaseUrl = AuthBaseUrl,
            Authority = "https://auth.lotro.test",
            ClientId = "lotrokoniecdev-web",
            CallbackPath = "/callback",
            SignedOutCallbackPath = "/signout-callback-oidc",
            Scopes = ["openid", "email", "profile"],
            CallerKey = CallerKey
        }));
        services.AddSingleton<IOptions<TranslationSystemSettings>>(Microsoft.Extensions.Options.Options.Create(
            new TranslationSystemSettings { BaseUrl = TranslationSystemBaseUrl, CallerKey = CallerKey }));
    }

    private static IHttpContextAccessor SignedInVisitor()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("sub", "user-1")],
            authenticationType: "Cookies"));

        // GetTokenAsync reads the token from the ticket IAuthenticationService returns, so the token is
        // supplied there, as in TranslationContentNegotiationAndAuthDelegatingHandlerTests.
        AuthenticationProperties properties = new();
        properties.StoreTokens([new AuthenticationToken { Name = AccessTokenName, Value = AccessToken }]);
        AuthenticationTicket ticket = new(principal, properties, authenticationScheme: "Cookies");

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        authenticationService
            .AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        ServiceCollection requestServices = new();
        requestServices.AddSingleton(authenticationService);
        requestServices.AddSingleton(Substitute.For<IDeadSessionRegistry>());

        DefaultHttpContext httpContext = new()
        {
            RequestServices = requestServices.BuildServiceProvider(),
            User = principal
        };
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(VisitorAddress);

        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => _sendCount;

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        public string? LastCallerKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            LastAuthorization = request.Headers.Authorization;
            LastCallerKey = request.Headers.TryGetValues(FrontendCallerHeaders.Key, out IEnumerable<string>? values)
                ? string.Join(",", values)
                : null;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
