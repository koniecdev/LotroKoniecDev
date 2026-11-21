using System.Security.Claims;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.TranslationSystem.API.Auth.CurrentUserAccessing;
using Microsoft.AspNetCore.Http;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Auth;

/// <summary>
/// Pins who spends the daily save budget (ADR-0072 §1, amended by #1488): a plain translator does, a
/// Reviewer and the Admin do not.
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
}
