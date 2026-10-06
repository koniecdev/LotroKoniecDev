using System.Security.Claims;
using LotroKoniecDev.AuthSystem.API.Extensions;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Extensions;

public sealed class ClaimsPrincipalExtensionsTests
{
    private const string SubjectClaimType = "sub";

    [Fact]
    public void FindUserId_WhenOnlySubIsPresent_ReturnsSub()
    {
        // Arrange
        ClaimsPrincipal principal = CreatePrincipal(new Claim(SubjectClaimType, "subject-id"));

        // Act
        string? userId = principal.FindUserId();

        // Assert
        userId.ShouldBe("subject-id");
    }

    [Fact]
    public void FindUserId_WhenOnlyNameIdentifierIsPresent_ReturnsNameIdentifier()
    {
        // Arrange
        ClaimsPrincipal principal = CreatePrincipal(new Claim(ClaimTypes.NameIdentifier, "name-identifier-id"));

        // Act
        string? userId = principal.FindUserId();

        // Assert
        userId.ShouldBe("name-identifier-id");
    }

    [Fact]
    public void FindUserId_WhenBothArePresent_PrefersNameIdentifier()
    {
        // Arrange
        ClaimsPrincipal principal = CreatePrincipal(
            new Claim(SubjectClaimType, "subject-id"),
            new Claim(ClaimTypes.NameIdentifier, "name-identifier-id"));

        // Act
        string? userId = principal.FindUserId();

        // Assert
        userId.ShouldBe("name-identifier-id");
    }

    [Fact]
    public void FindUserId_WhenNeitherIsPresent_ReturnsNull()
    {
        // Arrange
        ClaimsPrincipal principal = CreatePrincipal(new Claim("name", "lotrokoniecdev-api"));

        // Act
        string? userId = principal.FindUserId();

        // Assert
        userId.ShouldBeNull();
    }

    private static ClaimsPrincipal CreatePrincipal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Bearer"));
}
