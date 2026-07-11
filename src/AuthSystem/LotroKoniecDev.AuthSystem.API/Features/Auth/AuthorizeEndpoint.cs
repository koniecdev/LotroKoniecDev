using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed class AuthorizeEndpoint : IEndpoint
{
    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager)
    {
        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        AuthenticateResult cookieResult = await httpContext.AuthenticateAsync(
            IdentityConstants.ApplicationScheme);

        if (cookieResult is not { Succeeded: true })
        {
            if (request.HasPromptValue(PromptValues.None))
            {
                return Results.Forbid(
                    new AuthenticationProperties(new Dictionary<string, string?>
                    {
                        [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.LoginRequired,
                        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user is not logged in."
                    }),
                    [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
            }

            string returnUrl = httpContext.Request.PathBase
                + httpContext.Request.Path
                + QueryString.Create(
                    httpContext.Request.HasFormContentType
                        ? httpContext.Request.Form.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value))
                        : httpContext.Request.Query.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value)));

            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = returnUrl },
                [IdentityConstants.ApplicationScheme]);
        }

        string? userId = cookieResult.Principal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrEmpty(userId))
        {
            return Results.Challenge(
                new AuthenticationProperties
                {
                    RedirectUri = httpContext.Request.PathBase + httpContext.Request.Path
                        + QueryString.Create(
                            httpContext.Request.HasFormContentType
                                ? httpContext.Request.Form.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value))
                                : httpContext.Request.Query.Select(kvp => new KeyValuePair<string, string?>(kvp.Key, kvp.Value)))
                },
                [IdentityConstants.ApplicationScheme]);
        }

        ApplicationUser? user = await userManager.FindByIdAsync(userId);

        if (user is null)
        {
            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.LoginRequired,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user account is no longer valid."
                }),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        // A live session cookie must not keep minting tokens for an account that is
        // locked out or sitting in the GDPR deletion grace window (ADR-0031) — the
        // session is terminated and the client is sent back through the login page.
        if (user.DeletionScheduledAt is not null || await userManager.IsLockedOutAsync(user))
        {
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

            return Results.Forbid(
                new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.LoginRequired,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user account is no longer valid."
                }),
                [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
        }

        ClaimsIdentity identity = new(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(Claims.Email, user.Email);
        identity.SetClaim(Claims.Name, user.UserName);

        IList<string> roles = await userManager.GetRolesAsync(user);
        identity.SetClaims(Claims.Role, [.. roles]);

        identity.SetScopes(request.GetScopes());
        identity.SetResources(AuthConstants.ClientIds.Api);

        identity.SetDestinations(static claim => claim.Type switch
        {
            Claims.Subject => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Email => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Name => [Destinations.AccessToken, Destinations.IdentityToken],
            Claims.Role => [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken]
        });

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapMethods(
                "connect/authorize",
                [HttpMethods.Get, HttpMethods.Post],
                HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
