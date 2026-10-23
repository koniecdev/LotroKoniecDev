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
    /// So the not-found path takes as long as a wrong password and the timing reveals nothing.
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

        // OpenIddict allows the password grant only in Testing.
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

        // OpenIddict refuses a grant type that is not enabled, so this means a flow has no branch above.
        throw new InvalidOperationException($"The grant type '{request.GrantType}' is enabled but not handled.");
    }

    /// <summary>
    /// The account can change between /connect/authorize and the code exchange, and a password reset,
    /// lockout or scheduled deletion must not hand out an access token that works for minutes (#848).
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
        // The protocol's "username" carries the login identifier, which here is the e-mail (ADR-0022).
        ApplicationUser? user = await userManager.FindByEmailAsync(request.Username!);

        if (user is null)
        {
            _ = userManager.PasswordHasher.VerifyHashedPassword(
                new ApplicationUser(), DummyPasswordHash, request.Password!);
            return Refuse(InvalidCredentials);
        }

        // Before the lockout check, because a scheduled deletion also locks the account. The exact error
        // comes only after the password is verified, and a wrong password here does not count, as on the
        // login page (#861).
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
    /// OpenIddict refuses it first, and <see cref="OpenIddictTokenRefusals"/> writes the log line (#977).
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
    /// Every code exchange and refresh repeats these checks, because the account can change after the
    /// token was issued. Null means refused. The client gets the same answer for every case; only the
    /// log line names it (#944, #977).
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

        // Revocation at deletion scheduling is best effort, so these checks are the real guard. A scheduled
        // deletion also locks the account, so it is checked first for the more exact log line.
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

        // Revocation is best effort, so a token it missed would work again once the account is unlocked,
        // for example after a cancelled deletion (#848).
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
    /// OAuth clients read a refusal from "error" and "error_description" (RFC 6749 §5.2), not from
    /// ProblemDetails (#903).
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

    // Information, not Warning: each case below is the normal result of a change to the account (#977).
    [LoggerMessage(EventId = EventIds.TokenGrantRefusedUserGone, Level = LogLevel.Information, Message = "{Step} refused for user {UserId}: the account no longer exists")]
    private static partial void LogRefusedUserGone(ILogger logger, string step, string userId);

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedDeletionScheduled, Level = LogLevel.Information, Message = "{Step} refused for user {UserId}: account deletion is scheduled")]
    private static partial void LogRefusedDeletionScheduled(ILogger logger, string step, string userId);

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedLockedOut, Level = LogLevel.Information, Message = "{Step} refused for user {UserId}: the account is locked out")]
    private static partial void LogRefusedLockedOut(ILogger logger, string step, string userId);

    [LoggerMessage(EventId = EventIds.TokenGrantRefusedStaleSecurityStamp, Level = LogLevel.Information, Message = "{Step} refused for user {UserId}: the security stamp in the token is not current")]
    private static partial void LogRefusedStaleSecurityStamp(ILogger logger, string step, string userId);
}
