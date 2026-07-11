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

internal sealed class TokenEndpoint : IEndpoint
{
    /// <summary>
    /// Pre-computed hash for timing-equalization when user is not found.
    /// Prevents attackers from distinguishing "user not found" from "wrong password" via response time.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType())
        {
            return await HandleAuthorizationCodeGrantAsync(httpContext);
        }

        // Password flow is only enabled in Testing environment for integration/E2E tests.
        // OpenIddict will reject password grant requests in other environments.
        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(request, userManager, signInManager);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await HandleRefreshTokenGrantAsync(httpContext, userManager);
        }

        if (request.IsClientCredentialsGrantType())
        {
            return HandleClientCredentialsGrant(request);
        }

        return Results.Problem(
            title: "The specified grant type is not supported.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> HandleAuthorizationCodeGrantAsync(HttpContext httpContext)
    {
        AuthenticateResult result = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (result is not { Succeeded: true })
        {
            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "The authorization code is no longer valid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.SignIn(
            result.Principal!,
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> HandlePasswordGrantAsync(
        OpenIddictRequest request,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        // The OIDC "username" wire parameter is a protocol constant — semantically it carries
        // the login identifier, which is the e-mail (ADR-0022).
        ApplicationUser? user = await userManager.FindByEmailAsync(request.Username!);

        if (user is null)
        {
            // Perform a dummy password check to prevent timing-based user enumeration
            _ = userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, request.Password!);
            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "The email/password combination is invalid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A deletion-scheduled account is also locked out, so this gate must run before the
        // lockout-aware sign-in check. The specific error is revealed only after the password
        // is verified, keeping the endpoint unusable for account-state probing.
        if (user.DeletionScheduledAt is not null)
        {
            bool deletionScheduledPasswordValid = await userManager.CheckPasswordAsync(user, request.Password!);
            if (!deletionScheduledPasswordValid)
            {
                await userManager.AccessFailedAsync(user);
                return Results.Problem(
                    title: Errors.InvalidGrant,
                    detail: "The email/password combination is invalid.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "account_deletion_scheduled",
                statusCode: StatusCodes.Status400BadRequest);
        }

        SignInResult result = await signInManager.CheckPasswordSignInAsync(user, request.Password!, lockoutOnFailure: true);

        if (result.IsLockedOut || !result.Succeeded)
        {
            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "The email/password combination is invalid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        ClaimsIdentity identity = await CreateClaimsIdentityAsync(user, userManager, request);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> HandleRefreshTokenGrantAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager)
    {
        AuthenticateResult authenticateResult = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        string? userId = authenticateResult.Principal?.GetClaim(Claims.Subject);

        if (string.IsNullOrEmpty(userId))
        {
            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "The refresh token is no longer valid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        ApplicationUser? user = await userManager.FindByIdAsync(userId);

        if (user is null)
        {
            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "The refresh token is no longer valid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Refresh tokens are revoked when GDPR deletion is scheduled, but revocation is
        // best-effort — this gate guarantees a locked or deletion-scheduled account can
        // never refresh its way back to a usable access token.
        if (user.DeletionScheduledAt is not null || await userManager.IsLockedOutAsync(user))
        {
            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "The refresh token is no longer valid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        ClaimsIdentity identity = (ClaimsIdentity)authenticateResult.Principal!.Identity!;

        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(Claims.Email, user.Email);
        identity.SetClaim(Claims.Name, user.UserName);

        IList<string> roles = await userManager.GetRolesAsync(user);
        identity.SetClaims(Claims.Role, [.. roles]);

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

    private static IResult HandleClientCredentialsGrant(OpenIddictRequest request)
    {
        ClaimsIdentity identity = new(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, request.ClientId);
        identity.SetClaim(Claims.Name, request.ClientId);

        identity.SetScopes(request.GetScopes());
        identity.SetResources(AuthConstants.ClientIds.Api);

        identity.SetDestinations(static claim => claim.Type switch
        {
            Claims.Subject or Claims.Name => [Destinations.AccessToken, Destinations.IdentityToken],
            _ => [Destinations.AccessToken]
        });

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<ClaimsIdentity> CreateClaimsIdentityAsync(
        ApplicationUser user,
        UserManager<ApplicationUser> userManager,
        OpenIddictRequest request)
    {
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

        return identity;
    }

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("connect/token", HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
