using System.Collections.Immutable;
using LotroKoniecDev.SharedKernel.Authorization;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// The translation API and this API's account endpoints; this API refuses a token that does not name it
/// (#1023). Every path that issues a user's token sets this list, a new path too. The code exchange and
/// the refresh set it again, because refresh tokens slide and an old session would never get a changed
/// list.
/// </summary>
internal static class UserTokenAudiences
{
    public static ImmutableArray<string> All { get; } = [AuthConstants.ClientIds.Api, AuthConstants.Audiences.AuthApi];
}
