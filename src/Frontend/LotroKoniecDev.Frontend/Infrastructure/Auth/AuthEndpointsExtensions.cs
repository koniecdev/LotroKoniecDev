using LotroKoniecDev.Frontend.Infrastructure.Security;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Maps the login (challenge) and logout (cookie sign-out + RP-initiated end-session) endpoints the
/// OIDC RP wiring relies on. The navbar wires its login/logout controls to these in M3-02.
/// </summary>
internal static class AuthEndpointsExtensions
{
    private const string EndSessionPath = "connect/logout";
    private const string IdTokenName = "id_token";

    private static readonly Action<ILogger, Exception?> LogOidcAuthorityUnreachable =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(LogOidcAuthorityUnreachable)),
            "OIDC authority unreachable during login challenge; serving the login-unavailable page.");

    extension(IEndpointRouteBuilder endpoints)
    {
        public IEndpointRouteBuilder MapAuthEndpoints()
        {
            endpoints.MapGet(
                AuthenticationDependencyInjectionExtensions.LoginPath,
                LoginAsync);

            endpoints.MapPost(
                AuthenticationDependencyInjectionExtensions.LogoutPath,
                LogoutAsync);

            endpoints.MapPost(
                AuthenticationDependencyInjectionExtensions.LocalSignOutPath,
                LocalSignOutAsync);

            return endpoints;
        }
    }

    /// <summary>
    /// The login route's request delegate, exposed internally so it can be unit-tested without a web
    /// host. The OIDC challenge builds its redirect from the authority's discovery document, which the
    /// handler fetches lazily — when the auth server is unreachable that fetch throws deep inside
    /// <c>HandleChallenge</c> and would surface as a raw 500. Warming the discovery cache here first
    /// (the same <see cref="IConfigurationManager{T}"/> the handler uses) turns an outage into an honest
    /// "login temporarily unavailable" (503) page; the challenge below then reuses the now-cached
    /// document and 302s to the authority exactly as before when auth is up.
    /// </summary>
    internal static async Task<IResult> LoginAsync(
        HttpContext context,
        string? returnUrl,
        IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptionsMonitor,
        ILoggerFactory loggerFactory)
    {
        string redirectUri = LocalReturnUrl.Sanitize(returnUrl) ?? "/";

        IConfigurationManager<OpenIdConnectConfiguration>? configurationManager = openIdConnectOptionsMonitor
            .Get(OpenIdConnectDefaults.AuthenticationScheme)
            .ConfigurationManager;

        if (configurationManager is not null)
        {
            try
            {
                await configurationManager.GetConfigurationAsync(context.RequestAborted);
            }
            catch (Exception exception)
            {
                ILogger logger = loggerFactory.CreateLogger(typeof(AuthEndpointsExtensions).FullName!);
                LogOidcAuthorityUnreachable(logger, exception);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        return Results.Challenge(
            new AuthenticationProperties { RedirectUri = redirectUri },
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }

    /// <summary>
    /// Cookie-only sign-out for flows where the upstream IdP session is already dead — e.g. account
    /// deletion was just scheduled, so the auth server has locked the account and revoked its tokens.
    /// The regular <see cref="LogoutAsync"/> round-trips through OIDC end-session and always lands on
    /// the registered post-logout URI (home); this variant skips the dead round-trip and lands on the
    /// given local page instead (the anonymous "deletion scheduled" info page).
    /// </summary>
    internal static async Task<IResult> LocalSignOutAsync(HttpContext context, string? returnUrl)
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        string redirect = LocalReturnUrl.Sanitize(returnUrl) ?? "/";
        return Results.Redirect(redirect);
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext context,
        IOptions<AuthSystemSettings> authSystemOptions)
    {
        string? idToken = await context.GetTokenAsync(IdTokenName);

        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        string authority = authSystemOptions.Value.Authority.TrimEnd('/');
        string postLogoutRedirect = $"{context.Request.Scheme}://{context.Request.Host}";

        string endSessionUrl = $"{authority}/{EndSessionPath}" +
                               $"?post_logout_redirect_uri={Uri.EscapeDataString(postLogoutRedirect)}";

        if (!string.IsNullOrWhiteSpace(idToken))
        {
            endSessionUrl += $"&id_token_hint={Uri.EscapeDataString(idToken)}";
        }

        return Results.Redirect(endSessionUrl);
    }
}
