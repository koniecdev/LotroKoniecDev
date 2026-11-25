using System.Security.Claims;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Auth;

public sealed class ReadOnlyForVisitorsPolicyTests
{
    [Fact]
    public async Task Policy_ForAVisitorsGet_Succeeds()
    {
        // Arrange
        HttpContext request = Request(HttpMethods.Get);

        // Act
        AuthorizationResult result = await AuthorizeAsync(Visitor(), request);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    /// <summary>PUT never reaches the page; it shows that a method other than GET is refused too.</summary>
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    public async Task Policy_ForAVisitorsRequestThatIsNotAGet_Fails(string method)
    {
        // Arrange
        HttpContext request = Request(method);

        // Act
        AuthorizationResult result = await AuthorizeAsync(Visitor(), request);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Policy_ForASignedInUsersPost_Succeeds()
    {
        // Arrange
        HttpContext request = Request(HttpMethods.Post);
        ClaimsPrincipal signedIn = new(new ClaimsIdentity([new Claim("sub", "frodo")], authenticationType: "Cookies"));

        // Act
        AuthorizationResult result = await AuthorizeAsync(signedIn, request);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    /// <summary>The page checks its policy again while it draws, with no request, after the request passed.</summary>
    [Fact]
    public async Task Policy_ForAVisitorWithNoRequest_Succeeds()
    {
        // Act
        AuthorizationResult result = await AuthorizeAsync(Visitor(), resource: null);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    private static ClaimsPrincipal Visitor() => new(new ClaimsIdentity());

    private static DefaultHttpContext Request(string method) =>
        new() { Request = { Method = method } };

    private static async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource)
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddAuthorizationCore()
            .BuildServiceProvider();

        IAuthorizationService authorizationService = services.GetRequiredService<IAuthorizationService>();

        return await authorizationService.AuthorizeAsync(user, resource, ReadOnlyForVisitorsPolicy.Policy);
    }
}
