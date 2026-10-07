using System.Collections.Immutable;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The APIs a user's access token is for: the translation API and the account endpoints of this API.
/// This API refuses a token that does not name it (#1023). Every user sign-in and every refresh sets this
/// one list, so a new sign-in path cannot leave an API out.
/// </summary>
internal static class UserTokenAudiences
{
    public static ImmutableArray<string> All { get; } = [AuthConstants.ClientIds.Api, AuthConstants.Audiences.AuthApi];
}
