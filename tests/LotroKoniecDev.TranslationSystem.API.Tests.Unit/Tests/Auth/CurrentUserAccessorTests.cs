using System.Security.Claims;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Auth;

/// <summary>
/// Pins what the accessor reads from the token: who spends the daily save budget (ADR-0072 §1, amended
/// by #1488), and which subject is a user (#1543).
/// </summary>
public sealed class CurrentUserAccessorTests
{
    [Theory]
    [InlineData(new[] { AuthConstants.Roles.Translator }, true)]
    [InlineData(new[] { AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer }, false)]
    [InlineData(new[] { AuthConstants.Roles.Admin }, false)]
    public void HasDailySaveBudget_ForTheCallersRoles_IsTrueOnlyForAPlainTranslator(string[] roles, bool expected)
    {
        // Arrange
        ClaimsIdentity identity = new(
            roles.Select(role => new Claim("role", role)),
            authenticationType: "Bearer",
            nameType: "name",
            roleType: "role");
        HttpContextAccessor accessor = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        CurrentUserAccessor currentUser = new(accessor);

        // Act
        bool hasBudget = currentUser.HasDailySaveBudget();

        // Assert
        hasBudget.ShouldBe(expected);
    }

    [Fact]
    public void MaybeIdentityId_ForASubjectThatIsNotAGuid_HasNoValue()
    {
        // Arrange: a service token names its client, not a user, so the public list treats it as a
        // visitor (#1543).
        CurrentUserAccessor currentUser = AccessorFor(AuthConstants.ClientIds.Api);

        // Act
        bool hasUser = currentUser.MaybeIdentityId.HasValue;

        // Assert
        hasUser.ShouldBeFalse();
    }

    [Fact]
    public void MaybeIdentityId_ForAGuidSubject_IsThatUser()
    {
        // Arrange
        Guid subject = Guid.NewGuid();
        CurrentUserAccessor currentUser = AccessorFor(subject.ToString());

        // Act
        Guid userId = currentUser.MaybeIdentityId.Value.Value;

        // Assert
        userId.ShouldBe(subject);
    }

    private static CurrentUserAccessor AccessorFor(string subject)
    {
        ClaimsIdentity identity = new([new Claim("sub", subject)], authenticationType: "Bearer");
        HttpContextAccessor accessor = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
        return new CurrentUserAccessor(accessor);
    }
}
