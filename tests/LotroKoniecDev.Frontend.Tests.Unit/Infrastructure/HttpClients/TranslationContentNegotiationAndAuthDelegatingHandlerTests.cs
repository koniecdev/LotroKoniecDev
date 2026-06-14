using System.Net;
using System.Security.Claims;
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

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5004/translations");
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

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5004/health");
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_WhenUserIsAuthenticated_AttachesBearerAccessToken()
    {
        IHttpContextAccessor accessor = AuthenticatedContext(accessToken: "the-access-token");
        (HttpMessageInvoker invoker, StubHttpMessageHandler inner) = CreateInvoker(accessor);

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5004/translations");
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

        using HttpRequestMessage request = new(HttpMethod.Get, "https://localhost:5004/translations");
        await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.Authorization.ShouldBeNull();
    }

    private static (HttpMessageInvoker, StubHttpMessageHandler) CreateInvoker(IHttpContextAccessor accessor)
    {
        StubHttpMessageHandler inner = StubHttpMessageHandler.RespondWith(HttpStatusCode.OK, "{}");
        TranslationContentNegotiationAndAuthDelegatingHandler handler = new(accessor)
        {
            InnerHandler = inner
        };
        return (new HttpMessageInvoker(handler), inner);
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

    private static IHttpContextAccessor AuthenticatedContext(string? accessToken)
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim("sub", "user-1")],
            authenticationType: "Cookies"));

        // HttpContext.GetTokenAsync(name) is an extension that calls IAuthenticationService
        // .AuthenticateAsync and reads the token out of the resulting ticket's properties — so the
        // token is supplied via a successful AuthenticateResult, not a GetTokenAsync stub.
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
