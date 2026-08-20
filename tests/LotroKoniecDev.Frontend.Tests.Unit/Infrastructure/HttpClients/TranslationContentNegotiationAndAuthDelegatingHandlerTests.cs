using System.Net;
using System.Security.Claims;
using LotroKoniecDev.Frontend.Infrastructure.Auth.DeadSession;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients.TranslationSystemHttpClients;
using LotroKoniecDev.Hateoas.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.HttpClients;

public sealed class TranslationContentNegotiationAndAuthDelegatingHandlerTests
{
    private const string AccessTokenName = "access_token";

    [Fact]
    public async Task SendAsync_AlwaysRequestsTheHateoasRepresentation()
    {
        IHttpContextAccessor accessor = AnonymousContext();
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(accessor);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5002/translations");
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest.ShouldNotBeNull();
        inner.LastRequest!.Headers.Accept
            .ShouldContain(header => header.MediaType == MediaTypes.HateoasJson);
    }

    [Fact]
    public async Task SendAsync_WhenUserIsAnonymous_DoesNotAttachAuthorizationHeader()
    {
        IHttpContextAccessor accessor = AnonymousContext();
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(accessor);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5002/health");
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_WhenUserIsAuthenticated_AttachesBearerAccessToken()
    {
        IHttpContextAccessor accessor = AuthenticatedContext(accessToken: "the-access-token");
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(accessor);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5002/translations");
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Authorization.ShouldNotBeNull();
        inner.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        inner.LastRequest.Headers.Authorization.Parameter.ShouldBe("the-access-token");
    }

    [Fact]
    public async Task SendAsync_WhenAuthenticatedButNoTokenStored_DoesNotAttachAuthorizationHeader()
    {
        IHttpContextAccessor accessor = AuthenticatedContext(accessToken: null);
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(accessor);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5002/translations");
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_WhenAuthenticatedCallReturns401_MarksSessionDead()
    {
        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        IHttpContextAccessor accessor = AuthenticatedContext(
            accessToken: "the-access-token", deadSessionRegistry: deadSessionRegistry);
        (HttpMessageInvoker invoker, _) = CreateInvoker(
            accessor, StubHttpMessageHandler.RespondWith(HttpStatusCode.Unauthorized, "{}"));

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5002/translations");
        await invoker.SendAsync(request, CancellationToken.None);

        await deadSessionRegistry.Received(1).MarkDeadAsync("user-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenAuthenticatedCallSucceeds_DoesNotMarkSessionDead()
    {
        IDeadSessionRegistry deadSessionRegistry = Substitute.For<IDeadSessionRegistry>();
        IHttpContextAccessor accessor = AuthenticatedContext(
            accessToken: "the-access-token", deadSessionRegistry: deadSessionRegistry);
        (HttpMessageInvoker invoker, _) = CreateInvoker(accessor);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5002/translations");
        await invoker.SendAsync(request, CancellationToken.None);

        await deadSessionRegistry.DidNotReceive()
            .MarkDeadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_WhenAnonymousCallReturns401_DoesNotMarkSessionDead()
    {
        // An anonymous request returns before the 401 backstop (the handler short-circuits at the
        // IsAuthenticated guard), so the registry is never resolved and no subject is ever marked.
        IHttpContextAccessor accessor = AnonymousContext();
        (HttpMessageInvoker invoker, _) = CreateInvoker(
            accessor, StubHttpMessageHandler.RespondWith(HttpStatusCode.Unauthorized, "{}"));

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5002/translations");
        HttpResponseMessage response = await invoker.SendAsync(request, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Dispose();
    }

    private static (HttpMessageInvoker, StubHttpMessageHandler) CreateInvoker(
        IHttpContextAccessor accessor,
        StubHttpMessageHandler? inner = null)
    {
        StubHttpMessageHandler stub = inner ?? StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        TranslationContentNegotiationAndAuthDelegatingHandler handler = new(accessor)
        {
            InnerHandler = stub
        };
        return (new HttpMessageInvoker(handler), stub);
    }

    private static IHttpContextAccessor AnonymousContext()
    {
        DefaultHttpContext httpContext = new()
        {
            User = new ClaimsPrincipal(new ClaimsIdentity())
        };
        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }

    private static IHttpContextAccessor AuthenticatedContext(
        string? accessToken,
        IDeadSessionRegistry? deadSessionRegistry = null)
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("sub", "user-1")],
            authenticationType: "Cookies"));

        // HttpContext.GetTokenAsync(name) is an extension method that calls
        // IAuthenticationService.AuthenticateAsync and reads the token out of the resulting ticket. So
        // the token is supplied through a successful AuthenticateResult and not by stubbing
        // GetTokenAsync.
        AuthenticationProperties properties = new();
        if (accessToken is not null)
        {
            properties.StoreTokens([new AuthenticationToken { Name = AccessTokenName, Value = accessToken }]);
        }

        AuthenticationTicket ticket = new(principal, properties, authenticationScheme: "Cookies");

        IAuthenticationService authenticationService = Substitute.For<IAuthenticationService>();
        authenticationService
            .AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string?>())
            .Returns(AuthenticateResult.Success(ticket));

        ServiceCollection services = new();
        services.AddSingleton(authenticationService);
        services.AddSingleton(deadSessionRegistry ?? Substitute.For<IDeadSessionRegistry>());

        DefaultHttpContext httpContext = new()
        {
            RequestServices = services.BuildServiceProvider(),
            User = principal
        };

        IHttpContextAccessor accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);
        return accessor;
    }
}
