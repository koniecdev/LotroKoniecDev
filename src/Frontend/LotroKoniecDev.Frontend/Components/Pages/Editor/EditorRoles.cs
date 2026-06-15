namespace LotroKoniecDev.Frontend.Components.Pages.Editor;

/// <summary>
/// The role names the editor gates on, mirrored from the TMS API authorization policies
/// (<c>AuthConstants.Roles</c>): approving a translation requires the same role the API's
/// <c>RequireAdminRole</c> policy enforces on <c>POST /api/v1/translations/{id}/approve</c>. Held as a
/// Frontend-local constant — like <c>TranslationListQuery.Language</c> — so the UI gate is
/// self-describing and the Frontend does not depend on another bounded context's kernel for a single
/// claim value. The API remains the real gate; this only decides whether to render the control.
/// </summary>
internal static class EditorRoles
{
    /// <summary>The reviewer role that may approve translations (the API's <c>Admin</c> role).</summary>
    public const string Approver = "Admin";
}
