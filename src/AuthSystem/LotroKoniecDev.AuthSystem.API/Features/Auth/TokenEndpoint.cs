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
    /// A hash computed up front, so the not-found path takes as long as the normal one. Without it,
    /// response time would tell an attacker "no such user" from "wrong password".
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

        // The password flow is only on in the Testing environment, for integration and E2E tests.
        // OpenIddict refuses a password grant anywhere else.
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
        // "username" is a fixed name in the OIDC protocol. What it actually carries is the login
        // identifier, and here that is the e-mail (ADR-0022).
        ApplicationUser? user = await userManager.FindByEmailAsync(request.Username!);

        if (user is null)
        {
            // Check a dummy password anyway, so the response time does not reveal whether the user
            // exists.
            _ = userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, request.Password!);
            return Results.Problem(
                title: Errors.InvalidGrant,
                detail: "The email/password combination is invalid.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // An account with a scheduled deletion is also locked out, so this check has to come before
        // the sign-in check that looks at the lockout. The exact error is only shown after the password
        // was verified, so this endpoint cannot be used to learn the state of an account.
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

        // Refresh tokens are revoked when a GDPR deletion is scheduled, but that revocation is only
        // best effort. This check makes sure a locked account, or one waiting for deletion, can never
        // refresh its way back to a working access token.
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
