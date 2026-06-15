using LotroKoniecDev.Frontend.Components.Pages.Editor;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Editor;

public sealed class EditorRolesTests
{
    [Fact]
    public void Approver_MirrorsTheApiAdminRole()
    {
        // The editor only renders the approve control for this role; the API's RequireAdminRole policy
        // is the real gate. If the API role name ever changes, this guard fails before the UI silently
        // drifts (showing or hiding the button for the wrong users).
        EditorRoles.Approver.ShouldBe(AuthConstants.Roles.Admin);
    }
}
