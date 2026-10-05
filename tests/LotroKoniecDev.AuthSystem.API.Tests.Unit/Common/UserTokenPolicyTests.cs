using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// A service token is valid but names no account, so the account endpoints must refuse it (#966).
/// </summary>
public sealed class UserTokenPolicyTests
{
    private const string SubjectClaimType = "sub";

    [Theory]
    [InlineData("0b6f3a52-6a2e-4c1e-9f0d-3c2b1a0e9d8f")]
    [InlineData("0B6F3A52-6A2E-4C1E-9F0D-3C2B1A0E9D8F")]
    public async Task Policy_WhenTheSubjectIsAUserId_Succeeds(string subject)
    {
        // Arrange
        ClaimsPrincipal principal = CreateSignedInPrincipal(new Claim(SubjectClaimType, subject));

        // Act
        AuthorizationResult result = await AuthorizeAsync(principal);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("lotrokoniecdev-api")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Policy_WhenTheSubjectIsNotAUserId_Fails(string subject)
    {
        // Arrange
        ClaimsPrincipal principal = CreateSignedInPrincipal(new Claim(SubjectClaimType, subject));

        // Act
        AuthorizationResult result = await AuthorizeAsync(principal);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Policy_WhenTheUserIdComesAsANameIdentifier_Succeeds()
    {
        // Arrange: the account handlers read NameIdentifier before sub (FindUserId), so the policy accepts it too
        ClaimsPrincipal principal = CreateSignedInPrincipal(
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));

        // Act
        AuthorizationResult result = await AuthorizeAsync(principal);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Policy_WhenTheTokenCarriesNoSubject_Fails()
    {
        // Arrange
        ClaimsPrincipal principal = CreateSignedInPrincipal();

        // Act
        AuthorizationResult result = await AuthorizeAsync(principal);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Policy_WhenTheSubjectIsAUserIdButNobodySignedIn_Fails()
    {
        // Arrange: an identity with no authentication type is anonymous, whatever claims it carries
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(SubjectClaimType, Guid.NewGuid().ToString())]));

        // Act
        AuthorizationResult result = await AuthorizeAsync(principal);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    private static ClaimsPrincipal CreateSignedInPrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));

    private static async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal principal)
    {
        await using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddAuthorizationCore()
            .BuildServiceProvider();

        IAuthorizationService authorizationService = services.GetRequiredService<IAuthorizationService>();

        return await authorizationService.AuthorizeAsync(principal, resource: null, UserTokenPolicy.Policy);
    }
}
