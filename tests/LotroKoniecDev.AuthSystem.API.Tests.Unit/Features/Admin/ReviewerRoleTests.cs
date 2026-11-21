using LotroKoniecDev.AuthSystem.API.Features.Admin;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Admin;

/// <summary>
/// Pins who may get the Reviewer role (#1488): a translator that is not an admin, has no deletion
/// scheduled and is not a reviewer yet. The grant and its link both read this rule.
/// </summary>
public sealed class ReviewerRoleTests
{
    [Theory]
    [InlineData(new[] { AuthConstants.Roles.Translator }, false, true)]
    [InlineData(new[] { AuthConstants.Roles.Translator }, true, false)]
    [InlineData(new[] { AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer }, false, false)]
    [InlineData(new[] { AuthConstants.Roles.Translator, AuthConstants.Roles.Admin }, false, false)]
    [InlineData(new[] { AuthConstants.Roles.Admin }, false, false)]
    [InlineData(new string[0], false, false)]
    public void CanBeGranted_ForTheAccountsRolesAndState_IsTrueOnlyForAPlainTranslator(
        string[] roles,
        bool isDeletionScheduled,
        bool expected)
    {
        // Act
        bool canBeGranted = ReviewerRole.CanBeGranted(roles, isDeletionScheduled);

        // Assert
        canBeGranted.ShouldBe(expected);
    }
}
