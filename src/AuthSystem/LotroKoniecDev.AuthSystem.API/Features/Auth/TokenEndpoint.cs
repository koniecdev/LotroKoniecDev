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

internal sealed partial class TokenEndpoint : IEndpoint
{
    private const string AuthorizationCodeNoLongerValid = "The authorization code is no longer valid.";
    private const string InvalidCredentials = "The email/password combination is invalid.";
    private const string RefreshTokenNoLongerValid = "The refresh token is no longer valid.";

    /// <summary>
    /// A code, not a sentence, so a test or a client can match on it.
    /// </summary>
    private const string AccountDeletionScheduledCode = "account_deletion_scheduled";

    /// <summary>
    /// A hash computed up front, so the not-found path takes as long as the normal one. Without it,
    /// response time would tell an attacker "no such user" from "wrong password".
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), "DummyP@ssw0rd!");

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<TokenEndpoint> logger)
    {
        OpenIddictRequest request = httpContext.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType())
        {
            return await HandleAuthorizationCodeGrantAsync(httpContext, userManager, signInManager, logger);
        }

        // The password flow is only on in the Testing environment, for integration and E2E tests.
        // OpenIddict refuses a password grant anywhere else.
        if (request.IsPasswordGrantType())
        {
            return await HandlePasswordGrantAsync(request, userManager, signInManager);
        }

        if (request.IsRefreshTokenGrantType())
        {
            return await HandleRefreshTokenGrantAsync(httpContext, userManager, signInManager, logger);
        }

        if (request.IsClientCredentialsGrantType())
        {
            return HandleClientCredentialsGrant(request);
        }

        // OpenIddict refuses every grant type that is not enabled before this handler runs. Reaching
        // this line means a flow was enabled without a branch above.
        throw new InvalidOperationException($"The grant type '{request.GrantType}' is enabled but not handled.");
    }

    /// <summary>
    /// The code carries the stamp read at /connect/authorize, and the account can change in the seconds
    /// before the code is redeemed. A password reset, a lockout or a scheduled deletion must not hand out
    /// tokens: the access token would still work for five minutes (#848, ADR-0049).
    /// </summary>
    private static async Task<IResult> HandleAuthorizationCodeGrantAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<TokenEndpoint> logger)
    {
        AuthenticateResult result = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        ApplicationUser? user = await FindUserStillAllowedAsync(
            result.Principal, TokenGrantName.CodeExchange, userManager, signInManager, logger);

        if (user is null)
        {
            return Refuse(AuthorizationCodeNoLongerValid);
        }

        ClaimsPrincipal principal = result.Principal!;

        // Set again, not kept from the code. UserTokenAudiences says why.
        principal.SetResources(UserTokenAudiences.All);

        return Results.SignIn(
            principal,
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
            return Refuse(InvalidCredentials);
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
                return Refuse(InvalidCredentials);
            }

            return Refuse(AccountDeletionScheduledCode);
        }

        SignInResult result = await signInManager.CheckPasswordSignInAsync(user, request.Password!, lockoutOnFailure: true);

        if (result.IsLockedOut || !result.Succeeded)
        {
            return Refuse(InvalidCredentials);
        }

        ClaimsIdentity identity = await CreateClaimsIdentityAsync(user, userManager, request);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// A refresh token that expired, was revoked, or was used more than 30 seconds ago never gets here:
    /// OpenIddict refuses it first, and <see cref="OpenIddictTokenRefusals"/> writes the warning (#977).
    /// </summary>
    private static async Task<IResult> HandleRefreshTokenGrantAsync(
        HttpContext httpContext,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<TokenEndpoint> logger)
    {
        AuthenticateResult authenticateResult = await httpContext.AuthenticateAsync(
            OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        ApplicationUser? user = await FindUserStillAllowedAsync(
            authenticateResult.Principal, TokenGrantName.Refresh, userManager, signInManager, logger);

        if (user is null)
        {
            return Refuse(RefreshTokenNoLongerValid);
        }

        ClaimsIdentity identity = (ClaimsIdentity)authenticateResult.Principal!.Identity!;

        identity.SetClaim(Claims.Subject, user.Id.ToString());
        identity.SetClaim(Claims.Email, user.Email);
        identity.SetClaim(Claims.Name, user.UserName);

        IList<string> roles = await userManager.GetRolesAsync(user);
        identity.SetClaims(Claims.Role, [.. roles]);

        // Set again, not kept from the refresh token. UserTokenAudiences says why.
        identity.SetResources(UserTokenAudiences.All);

        identity.SetDestinations(UserClaimDestinations.Select);

        return Results.SignIn(
            new ClaimsPrincipal(identity),
            authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// The account checks that every code exchange and every refresh repeats, because the account can
    /// change after the token was issued. Null means refused. The caller sends the same answer for every
    /// case, so a client learns nothing about the account; only the warning here names the case (#944).
    /// Both grants share this one check, so they cannot drift apart (#977).
    /// </summary>
    private static async Task<ApplicationUser?> FindUserStillAllowedAsync(
        ClaimsPrincipal? principal,
        TokenGrantName grant,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<TokenEndpoint> logger)
    {
        if (principal is null || principal.GetClaim(Claims.Subject) is not { Length: > 0 } userId)
        {
            LogRefusedNoSubject(logger, grant.Step, grant.Token);
            return null;
        }

        ApplicationUser? user = await userManager.FindByIdAsync(userId);

        if (user is null)
        {
            LogRefusedUserGone(logger, grant.Step, userId);
            return null;
        }

        // Tokens are revoked when a GDPR deletion is scheduled, but that revocation is only best effort.
        // These checks make sure a locked account, or one waiting for deletion, can never trade a token
        // for a working access token. A scheduled deletion also locks the account, so it is checked first
        // to get the more exact warning.
        if (user.DeletionScheduledAt is not null)
        {
            LogRefusedDeletionScheduled(logger, grant.Step, userId);
            return null;
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            LogRefusedLockedOut(logger, grant.Step, userId);
            return null;
        }

        // Every flow that ends all sessions changes the stamp, and its token revocation is only best
        // effort. Without this check a token the revoke missed works again as soon as the account is
        // unlocked, for example when a scheduled deletion is cancelled (#848).
        if (!await SessionSecurityStamp.IsCurrentAsync(principal, user, signInManager))
        {
            LogRefusedStaleSecurityStamp(logger, grant.Step, userId);
            return null;
        }

        return user;
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
        identity.SetResources(UserTokenAudiences.All);

        identity.SetDestinations(UserClaimDestinations.Select);

        return identity;
    }

    /// <summary>
    /// OAuth clients read a refusal from the standard "error" and "error_description" fields
    /// (RFC 6749 §5.2), not from ProblemDetails. With ProblemDetails, the frontend's OpenID Connect
    /// handler logged a failed sign-in with an empty reason (#903).
    /// </summary>
    private static IResult Refuse(string description) =>
        Results.Forbid(
            new AuthenticationProperties(new Dictionary<string, string?>
            {
                [OpenIddictServerAspNetCoreConstants.Properties.Error] = Errors.InvalidGrant,
                [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
            }),
            [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    public void MapEndpoint(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapPost("connect/token", HandleAsync)
            .AllowAnonymous()
            .ExcludeFromDescription();
    }

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedNoSubject, Level = LogLevel.Warning, Message = "{Step} refused: no user id could be read from the {Token}")]
    private static partial void LogRefusedNoSubject(ILogger logger, string step, string token);

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedUserGone, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId}: the account no longer exists")]
    private static partial void LogRefusedUserGone(ILogger logger, string step, string userId);

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedDeletionScheduled, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId}: account deletion is scheduled")]
    private static partial void LogRefusedDeletionScheduled(ILogger logger, string step, string userId);

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedLockedOut, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId}: the account is locked out")]
    private static partial void LogRefusedLockedOut(ILogger logger, string step, string userId);

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedStaleSecurityStamp, Level = LogLevel.Warning, Message = "{Step} refused for user {UserId}: the security stamp in the token is not current")]
    private static partial void LogRefusedStaleSecurityStamp(ILogger logger, string step, string userId);
}
