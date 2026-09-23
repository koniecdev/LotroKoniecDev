using System.Net;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.AuthSystemHttpClients;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

/// <summary>
/// ADR-0054 (#819, #823): every auth API and TMS API call leaves from the Frontend's one container, so
/// the handler sends the visitor's address, the one <c>UseForwardedHeaders</c> resolved, next to the
/// environment's key, and the API meters the call on that visitor. The first tests drive the handler
/// alone; the last four go through the real registrations, one per path to either API, because a
/// handler that is written but not wired changes nothing.
/// </summary>
public sealed class FrontendCallerDelegatingHandlerTests
{
    // Built rather than written out, so no secret scanner mistakes test data for a key.
    private static readonly string CallerKey = new('k', 40);

    // A different value from the auth API's, so a client that read the wrong settings would show it.
    private static readonly string TranslationSystemCallerKey = new('t', 40);

    private const string VisitorAddress = "203.0.113.7";
    private const string AuthBaseUrl = "https://auth.lotro.test/";
    private const string TranslationSystemBaseUrl = "https://tms.lotro.test/";

    [Fact]
    public async Task SendAsync_WithAKeyAndAVisitor_SendsBothHeaders()
    {
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(AccessorFor(VisitorAddress), CallerKey);

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl + "auth/account/data-export");
        await invoker.SendAsync(request, CancellationToken.None);

        HeaderValues(inner.LastRequest!, FrontendCallerHeaders.Key).ShouldBe([CallerKey]);
        HeaderValues(inner.LastRequest!, FrontendCallerHeaders.ClientAddress).ShouldBe([VisitorAddress]);
    }

    [Fact]
    public async Task SendAsync_WithAnIPv6Visitor_SendsTheCanonicalForm()
    {
        // The same string the auth API gets for a direct IPv6 caller, so one visitor is one bucket.
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(AccessorFor("2001:db8::1"), CallerKey);

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
        await invoker.SendAsync(request, CancellationToken.None);

        HeaderValues(inner.LastRequest!, FrontendCallerHeaders.ClientAddress).ShouldBe(["2001:db8::1"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_WithNoKeyConfigured_SendsNeitherHeader(string? callerKey)
    {
        // A visitor address is there to forward; only the key is missing, and without it the auth API
        // would ignore the address anyway.
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(AccessorFor(VisitorAddress), callerKey);

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Contains(FrontendCallerHeaders.Key).ShouldBeFalse();
        inner.LastRequest.Headers.Contains(FrontendCallerHeaders.ClientAddress).ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_OutsideARequest_SendsNeitherHeader()
    {
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(accessor, CallerKey);

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Contains(FrontendCallerHeaders.Key).ShouldBeFalse();
        inner.LastRequest.Headers.Contains(FrontendCallerHeaders.ClientAddress).ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_WithNoConnectionAddress_SendsNeitherHeader()
    {
        // The key alone proves nothing without an address, so neither header is worth sending.
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(AccessorFor(address: null), CallerKey);

        using HttpRequestMessage request = new(HttpMethod.Get, AuthBaseUrl);
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Contains(FrontendCallerHeaders.Key).ShouldBeFalse();
        inner.LastRequest.Headers.Contains(FrontendCallerHeaders.ClientAddress).ShouldBeFalse();
    }

    [Fact]
    public async Task AuthSystemClient_ThroughTheRealPipeline_SendsEachHeaderOnceEvenWhenRetried()
    {
        // The typed client's pipeline retries a server error. The handler sits outside the resilience
        // handler, so the retried message carries one value per header; two values would make the auth
        // API drop the address and meter the call on this container instead.
        RecordingHttpMessageHandler primary = new(HttpStatusCode.InternalServerError);
        await using ServiceProvider provider = BuildProvider(primary, services => services.AddHttpClients());
        IAuthSystemClient client = provider.GetRequiredService<IAuthSystemClient>();

        await client.GetApiResultAsync<object>("auth/account/data-export");

        primary.SendCount.ShouldBeGreaterThan(1);
        HeaderValues(primary.LastRequest!, FrontendCallerHeaders.Key).ShouldBe([CallerKey]);
        HeaderValues(primary.LastRequest!, FrontendCallerHeaders.ClientAddress).ShouldBe([VisitorAddress]);
    }

    [Fact]
    public async Task TranslationSystemClient_ThroughTheRealPipeline_SendsItsOwnKeyOnceEvenWhenRetried()
    {
        // #823: every translator page load calls the TMS API from this container, so the TMS client
        // carries the visitor too, with the key from the TMS settings. It sits outside the resilience
        // handler as well, so a retried message still carries one value per header.
        RecordingHttpMessageHandler primary = new(HttpStatusCode.InternalServerError);
        await using ServiceProvider provider = BuildProvider(primary, services => services.AddHttpClients());
        ITranslationSystemClient client = provider.GetRequiredService<ITranslationSystemClient>();

        await client.GetDiscoveryAsync();

        primary.SendCount.ShouldBeGreaterThan(1);
        HeaderValues(primary.LastRequest!, FrontendCallerHeaders.Key).ShouldBe([TranslationSystemCallerKey]);
        HeaderValues(primary.LastRequest!, FrontendCallerHeaders.ClientAddress).ShouldBe([VisitorAddress]);
    }

    [Fact]
    public async Task TokenEndpointClient_ThroughTheRealRegistration_SendsBothHeaders()
    {
        // The refresh grant is the auth API call every near-expiry request makes, and it is not the typed
        // account client, so it is wired separately and proved separately.
        RecordingHttpMessageHandler primary = new(HttpStatusCode.OK);
        await using ServiceProvider provider = BuildProvider(primary, services => services.AddFrontendAuthentication());
        ITokenEndpointClient client = provider.GetRequiredService<ITokenEndpointClient>();

        await client.RefreshAsync("the-refresh-token");

        HeaderValues(primary.LastRequest!, FrontendCallerHeaders.Key).ShouldBe([CallerKey]);
        HeaderValues(primary.LastRequest!, FrontendCallerHeaders.ClientAddress).ShouldBe([VisitorAddress]);
    }

    [Fact]
    public async Task OpenIdConnectBackchannel_ThroughTheRealRegistration_SendsBothHeaders()
    {
        // The code exchange and the userinfo call use the OIDC handler's own back-channel client. A
        // handler put on that seam before the frontend's registration stands in for the transport, and
        // the frontend's registration has to wrap it rather than replace it.
        RecordingHttpMessageHandler transport = new(HttpStatusCode.OK);
        await using ServiceProvider provider = BuildProvider(transport, services =>
        {
            services.Configure<OpenIdConnectOptions>(
                OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = transport);
            services.AddFrontendAuthentication();
        });
        OpenIdConnectOptions options = provider
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OpenIdConnectDefaults.AuthenticationScheme);

        using HttpRequestMessage request = new(HttpMethod.Post, AuthBaseUrl + "connect/token");
        using HttpResponseMessage response = await options.Backchannel.SendAsync(request);

        HeaderValues(transport.LastRequest!, FrontendCallerHeaders.Key).ShouldBe([CallerKey]);
        HeaderValues(transport.LastRequest!, FrontendCallerHeaders.ClientAddress).ShouldBe([VisitorAddress]);
    }

    private static IHttpContextAccessor AccessorFor(string? address)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);

        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private static (HttpMessageInvoker Invoker, StubHttpMessageHandler Inner) CreateInvoker(
        IHttpContextAccessor accessor,
        string? callerKey)
    {
        StubHttpMessageHandler inner = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        FrontendCallerDelegatingHandler handler = new(accessor, callerKey)
        {
            InnerHandler = inner
        };

        return (new HttpMessageInvoker(handler), inner);
    }

    /// <summary>
    /// The frontend's own registrations with the socket handler swapped for a recording stub, the way
    /// HttpClientsResilienceTests does it. A later ConfigurePrimaryHttpMessageHandler on the same named
    /// client wins, so the delegating handlers and the pipeline stay exactly as Program.cs wires them.
    /// </summary>
    private static ServiceProvider BuildProvider(HttpMessageHandler primary, Action<IServiceCollection> register)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(AccessorFor(VisitorAddress));
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<IOptions<AuthSystemSettings>>(Microsoft.Extensions.Options.Options.Create(SettingsWith(CallerKey)));
        services.AddSingleton<IOptions<TranslationSystemSettings>>(Microsoft.Extensions.Options.Options.Create(
            new TranslationSystemSettings { BaseUrl = TranslationSystemBaseUrl, CallerKey = TranslationSystemCallerKey }));
        register(services);
        services.AddHttpClient<IAuthSystemClient, AuthSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);
        services.AddHttpClient<ITranslationSystemClient, TranslationSystemClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);
        services.AddHttpClient<ITokenEndpointClient, TokenEndpointClient>()
            .ConfigurePrimaryHttpMessageHandler(() => primary);

        return services.BuildServiceProvider();
    }

    private static AuthSystemSettings SettingsWith(string? callerKey) => new()
    {
        BaseUrl = AuthBaseUrl,
        Authority = "https://auth.lotro.test",
        ClientId = "lotrokoniecdev-web",
        CallbackPath = "/callback",
        SignedOutCallbackPath = "/signout-callback-oidc",
        Scopes = ["openid", "email", "profile"],
        CallerKey = callerKey
    };

    private static string[] HeaderValues(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.ToArray() : [];

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private int _sendCount;

        public RecordingHttpMessageHandler(HttpStatusCode statusCode)
        {
            _statusCode = statusCode;
        }

        public int SendCount => _sendCount;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
