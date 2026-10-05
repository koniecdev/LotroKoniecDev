using LotroKoniecDev.AuthSystem.API.Middleware;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

/// <summary>
/// A refused sign-in or sign-out link reaches a program as problem details, and only this tells the
/// program why (#912).
/// </summary>
public sealed class OpenIddictErrorProblemDetailsTests
{
    [Theory]
    [InlineData(OpenIddictServerEndpointType.Authorization)]
    [InlineData(OpenIddictServerEndpointType.EndSession)]
    public void Add_ShouldCopyTheOAuthErrorButNotTheState_WhenOpenIddictRefusedASignInOrSignOutLink(
        OpenIddictServerEndpointType endpointType)
    {
        // Arrange
        ProblemDetailsContext context = BuildContext(endpointType, new OpenIddictResponse
        {
            Error = "invalid_request",
            ErrorDescription = "The specified 'client_id' is invalid.",
            ErrorUri = "https://documentation.openiddict.com/errors/ID2052",
            State = "caller-state"
        });

        // Act
        OpenIddictErrorProblemDetails.Add(context);

        // Assert
        context.ProblemDetails.Extensions.ShouldBe(new Dictionary<string, object?>
        {
            ["error"] = "invalid_request",
            ["error_description"] = "The specified 'client_id' is invalid.",
            ["error_uri"] = "https://documentation.openiddict.com/errors/ID2052"
        }, ignoreOrder: true);
    }

    /// <summary>
    /// Userinfo leaves its body to the status-code pages too. A request there with no token must get no
    /// error code (RFC 6750 §3.1), so nothing is copied for any endpoint but authorize and logout.
    /// </summary>
    [Theory]
    [InlineData(OpenIddictServerEndpointType.UserInfo)]
    [InlineData(OpenIddictServerEndpointType.Token)]
    [InlineData(OpenIddictServerEndpointType.Introspection)]
    [InlineData(OpenIddictServerEndpointType.Revocation)]
    [InlineData(OpenIddictServerEndpointType.Unknown)]
    public void Add_ShouldAddNothing_OnAnyOtherEndpoint(OpenIddictServerEndpointType endpointType)
    {
        // Arrange
        ProblemDetailsContext context = BuildContext(endpointType, new OpenIddictResponse
        {
            Error = "missing_token",
            ErrorDescription = "The mandatory 'Authorization' header is missing."
        });

        // Act
        OpenIddictErrorProblemDetails.Add(context);

        // Assert
        context.ProblemDetails.Extensions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Add_ShouldCopyOnlyTheError_WhenOpenIddictGaveNoDescriptionOrUri(string? missing)
    {
        // Arrange
        ProblemDetailsContext context = BuildContext(OpenIddictServerEndpointType.Authorization, new OpenIddictResponse
        {
            Error = "invalid_request",
            ErrorDescription = missing,
            ErrorUri = missing
        });

        // Act
        OpenIddictErrorProblemDetails.Add(context);

        // Assert
        context.ProblemDetails.Extensions.Keys.ShouldBe(["error"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Add_ShouldAddNothing_WhenOpenIddictsAnswerIsNoError(string? error)
    {
        // Arrange
        ProblemDetailsContext context = BuildContext(
            OpenIddictServerEndpointType.Authorization,
            new OpenIddictResponse { Error = error, ErrorDescription = "unused" });

        // Act
        OpenIddictErrorProblemDetails.Add(context);

        // Assert
        context.ProblemDetails.Extensions.ShouldBeEmpty();
    }

    [Fact]
    public void Add_ShouldAddNothing_WhenOpenIddictHasNotAnswered()
    {
        // Arrange: a 404 or an exception anywhere else in the app
        ProblemDetailsContext context = new() { HttpContext = new DefaultHttpContext() };

        // Act
        OpenIddictErrorProblemDetails.Add(context);

        // Assert
        context.ProblemDetails.Extensions.ShouldBeEmpty();
    }

    private static ProblemDetailsContext BuildContext(
        OpenIddictServerEndpointType endpointType,
        OpenIddictResponse response)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Features.Set(new OpenIddictServerAspNetCoreFeature
        {
            Transaction = new OpenIddictServerTransaction { EndpointType = endpointType, Response = response }
        });

        return new ProblemDetailsContext { HttpContext = httpContext };
    }
}
