namespace LotroKoniecDev.Frontend.Components.Pages.ImportExport;

/// <summary>
/// The role names the import/export page gates on, mirrored from the TMS API authorization policies
/// (<c>AuthConstants.Roles</c>): uploading a fresh export requires the same role the API's
/// <c>RequireAdminRole</c> policy enforces on <c>POST /api/v1/game-versions/{id}/import</c>. Held as a
/// Frontend-local constant — like <see cref="Editor.EditorRoles"/> — so the UI gate is
/// self-describing and the Frontend does not depend on another bounded context's kernel for a single
/// claim value. The API remains the real gate; this only decides whether to render the import panel.
/// Downloading the artifact is open to any authenticated translator (the API endpoint is anonymous).
/// </summary>
internal static class ImportExportRoles
{
    /// <summary>The admin role that may import a new export (the API's <c>Admin</c> role).</summary>
    public const string Importer = "Admin";
}
