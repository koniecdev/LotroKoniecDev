using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Features.Admin;

/// <summary>
/// The one rule for who may get the Reviewer role, shared by the grant and its link (#1488). A reviewer
/// keeps Translator, because every translation endpoint asks for it. An admin already has all a reviewer
/// has, and an account waiting for deletion is about to go.
/// </summary>
internal static class ReviewerRole
{
    public static bool CanBeGranted(IEnumerable<string> roles, bool isDeletionScheduled)
    {
        IReadOnlyCollection<string> heldRoles = [.. roles];

        return !isDeletionScheduled
               && heldRoles.Contains(AuthConstants.Roles.Translator)
               && !heldRoles.Contains(AuthConstants.Roles.Admin)
               && !heldRoles.Contains(AuthConstants.Roles.Reviewer);
    }
}
