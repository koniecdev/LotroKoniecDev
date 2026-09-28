using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LotroKoniecDev.AuthSystem.API.Features.Auth;

internal sealed class TokenEndpoint : IEndpoint
{
    private const string AuthorizationCodeNoLongerValid = "The authorization code is no longer valid.";
    private const string InvalidCredentials = "The email/password combination is invalid.";
    private const string RefreshTokenNoLongerValid = "The refresh token is no longer valid.";

    /// <summary>
    /// A code, not a sentence: a client matches on it to show the "scheduled for deletion" state.
    /// </summary>
    private const string AccountDeletionScheduled = "account_deletion_scheduled";

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
            return await HandleAuthorizationCodeGrantAsync(httpContext, userManager, signInManager);
        }

        // The password flow is only on in the Testing environment, for integration and E2E tests.
        // OpenIddict refuses a password grant anywhere else.
        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(request, userManager, signInManager);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await HandleRefreshTokenGrantAsync(httpContext, userManager, signInManager);
        }

        if (request.IsClientCredentialsGrantType())
        {
            return HandleClientCredentialsGrant(request);
        }

        return Refuse(Errors.UnsupportedGrantType, "The specified grant type is not supported.");
    }

    private static async Task<IResult> HandleAuthorizationCodeGrantAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        AuthenticateResult result = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (result is not { Succeeded: true })
        {
            return Refuse(Errors.InvalidGrant, AuthorizationCodeNoLongerValid);
        }

        // The code carries the stamp read at /connect/authorize, and the account can change in the
        // seconds before the code is redeemed. A password reset, a lockout or a scheduled deletion must
        // not hand out tokens: the access token would still work for five minutes (#848, ADR-0049).
        string? userId = result.Principal.GetClaim(Claims.Subject);
        ApplicationUser? user = string.IsNullOrEmpty(userId) ? null : await userManager.FindByIdAsync(userId);

        if (user is null
            || user.DeletionScheduledAt is not null
            || await userManager.IsLockedOutAsync(user)
            || !await SessionSecurityStamp.IsCurrentAsync(result.Principal, user, signInManager))
        {
            return Refuse(Errors.InvalidGrant, AuthorizationCodeNoLongerValid);
        }

        return Results.SignIn(
            result.Principal,
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
            return Refuse(Errors.InvalidGrant, InvalidCredentials);
        }

        // An account with a scheduled deletion is also locked out, so this check has to come before
        // the sign-in check that looks at the lockout. The exact error is only shown after the password
        // was verified, so this endpoint cannot be used to learn the state of an account. A wrong password
        // here does not count, for the same reason as on the login page (#861).
        if (user.DeletionScheduledAt is not null)
        {
            bool deletionScheduledPasswordValid = await userManager.CheckPasswordAsync(user, request.Password!);
            if (!deletionScheduledPasswordValid)
            {
                return Refuse(Errors.InvalidGrant, InvalidCredentials);
            }

            return Refuse(Errors.InvalidGrant, AccountDeletionScheduled);
        }

        SignInResult result = await signInManager.CheckPasswordSignInAsync(user, request.Password!, lockoutOnFailure: true);

        if (result.IsLockedOut || !result.Succeeded)
        {
            return Refuse(Errors.InvalidGrant, InvalidCredentials);
        }

        ClaimsIdentity identity = await CreateClaimsIdentityAsync(user, userManager, request);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> HandleRefreshTokenGrantAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager)
    {
        AuthenticateResult authenticateResult = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        string? userId = authenticateResult.Principal?.GetClaim(Claims.Subject);

        if (string.IsNullOrEmpty(userId))
        {
            return Refuse(Errors.InvalidGrant, RefreshTokenNoLongerValid);
        }

        ApplicationUser? user = await userManager.FindByIdAsync(userId);

        if (user is null)
        {
            return Refuse(Errors.InvalidGrant, RefreshTokenNoLongerValid);
        }

        // Refresh tokens are revoked when a GDPR deletion is scheduled, but that revocation is only
        // best effort. This check makes sure a locked account, or one waiting for deletion, can never
        // refresh its way back to a working access token.
        if (user.DeletionScheduledAt is not null || await userManager.IsLockedOutAsync(user))
        {
            return Refuse(Errors.InvalidGrant, RefreshTokenNoLongerValid);
        }

        // Every flow that ends all sessions changes the stamp, and its token revocation is only best
        // effort. Without this check a token the revoke missed works again as soon as the account is
        // unlocked, for example when a scheduled deletion is cancelled (#848).
        if (!await SessionSecurityStamp.IsCurrentAsync(authenticateResult.Principal!, user, signInManager))
        {
            return Refuse(Errors.InvalidGrant, RefreshTokenNoLongerValid);
        }

        ClaimsIdentity identity = (ClaimsIdentity)authenticateResult.Principal!.Identity!;

        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(Claims.Email, user.Email);
        identity.SetClaim(Claims.Name, user.UserName);

        IList<string> roles = await userManager.GetRolesAsync(user);
        identity.SetClaims(Claims.Role, [.. roles]);

        identity.SetDestinations(UserClaimDestinations.Select);

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

        await SessionSecurityStamp.AddAsync(identity, user, userManager);

        identity.SetScopes(request.GetScopes());
        identity.SetResources(AuthConstants.ClientIds.Api);

        identity.SetDestinations(UserClaimDestinations.Select);

        return identity;
    }

    /// <summary>
    /// OAuth clients read refusals from the standard error body (RFC 6749 §5.2), not from ProblemDetails.
    /// The frontend's OpenID Connect handler reads only "error", so a ProblemDetails body left its log
    /// with an empty reason (#903).
    /// </summary>
    private static IResult Refuse(string error, string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("connect/token", HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
