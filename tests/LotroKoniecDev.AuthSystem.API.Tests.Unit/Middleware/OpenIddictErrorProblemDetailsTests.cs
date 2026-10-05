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
    [Fact]
    public void Add_ShouldCopyTheOAuthErrorButNotTheState_WhenOpenIddictRefusedTheRequest()
    {
        // Arrange
        ProblemDetailsContext context = BuildContext(new OpenIddictResponse
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Add_ShouldCopyOnlyTheError_WhenOpenIddictGaveNoDescriptionOrUri(string? missing)
    {
        // Arrange
        ProblemDetailsContext context = BuildContext(new OpenIddictResponse
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
        ProblemDetailsContext context = BuildContext(new OpenIddictResponse { Error = error, ErrorDescription = "unused" });

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

    private static ProblemDetailsContext BuildContext(OpenIddictResponse response)
    {
        DefaultHttpContext httpContext = new();
        httpContext.Features.Set(new OpenIddictServerAspNetCoreFeature
        {
            Transaction = new OpenIddictServerTransaction { Response = response }
        });

        return new ProblemDetailsContext { HttpContext = httpContext };
    }
}
