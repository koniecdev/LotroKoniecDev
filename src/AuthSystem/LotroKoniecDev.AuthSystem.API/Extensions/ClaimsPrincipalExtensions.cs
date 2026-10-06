using System.Security.Claims;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Extensions;

/// <summary>
/// The one place the account endpoints and <see cref="Common.UserTokenPolicy"/> read the caller's user
/// id. So the policy always checks the claim that the handler then gets (#966).
/// </summary>
internal static class ClaimsPrincipalExtensions
{
    extension(ClaimsPrincipal principal)
    {
        public string? FindUserId() =>
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue(Claims.Subject);
    }
}
