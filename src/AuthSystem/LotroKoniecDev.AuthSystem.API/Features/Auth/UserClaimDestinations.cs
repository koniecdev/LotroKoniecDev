using System.Security.Claims;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

/// <summary>
/// One selector for every sign-in and refresh, so a new path cannot forget to keep the security stamp
/// out of the tokens a client reads (#848).
/// </summary>
internal static class UserClaimDestinations
{
    public static IEnumerable<string> Select(Claim claim) => claim.Type switch
    {
        Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
        Claims.Email => [Destinations.AccessToken, Destinations.IdentityToken],
        Claims.Name => [Destinations.AccessToken, Destinations.IdentityToken],
        Claims.Role => [Destinations.AccessToken, Destinations.IdentityToken],
        SessionSecurityStamp.ClaimType => [],
        _ => [Destinations.AccessToken]
    };
}
