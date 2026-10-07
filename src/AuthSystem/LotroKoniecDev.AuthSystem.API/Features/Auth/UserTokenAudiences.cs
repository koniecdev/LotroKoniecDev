using System.Collections.Immutable;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The APIs a user's access token is for: the translation API and the account endpoints of this API.
/// This API refuses a token that does not name it (#1023). Every path that issues a user's token sets
/// this one list, and a new path must set it too. The code exchange and the refresh set it again
/// and do not keep the list the code or the refresh token carries. Refresh tokens slide, so a session
/// that started before a change to the list would otherwise never get the new one.
/// </summary>
internal static class UserTokenAudiences
{
    public static ImmutableArray<string> All { get; } = [AuthConstants.ClientIds.Api, AuthConstants.Audiences.AuthApi];
}
