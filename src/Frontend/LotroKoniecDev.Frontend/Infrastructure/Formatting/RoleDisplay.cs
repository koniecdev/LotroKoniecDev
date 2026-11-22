using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.Frontend.Infrastructure.Formatting;

/// <summary>
/// The Polish name of a role, shared by "Moje konto" and the admin's account list. An unknown role keeps
/// its own name.
/// </summary>
internal static class RoleDisplay
{
    public static string Label(string role) => role switch
    {
        AuthConstants.Roles.Admin => "Administrator",
        AuthConstants.Roles.Translator => "Tłumacz",
        AuthConstants.Roles.Reviewer => "Recenzent",
        _ => role
    };
}
