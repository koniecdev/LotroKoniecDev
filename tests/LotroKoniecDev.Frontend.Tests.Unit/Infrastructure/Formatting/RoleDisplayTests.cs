using LotroKoniecDev.Frontend.Infrastructure.Formatting;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Formatting;

public sealed class RoleDisplayTests
{
    [Theory]
    [InlineData(AuthConstants.Roles.Admin, "Administrator")]
    [InlineData(AuthConstants.Roles.Translator, "Tłumacz")]
    [InlineData(AuthConstants.Roles.Reviewer, "Recenzent")]
    [InlineData("NotARole", "NotARole")]
    public void Label_ForARole_ReturnsItsPolishName(string role, string expected)
    {
        // Act
        string label = RoleDisplay.Label(role);

        // Assert
        label.ShouldBe(expected);
    }
}
