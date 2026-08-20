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
/// Maps the login and logout routes the OIDC setup needs. Login starts the challenge; logout clears the
/// cookie and ends the session at the auth server. The navbar's login and logout buttons point here
/// (M3-02).
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
    /// The login route's handler, internal so a unit test can call it without a web host.
    /// The OIDC challenge builds its redirect from the authority's discovery document, which the handler
    /// fetches the first time it needs it. When the auth server is unreachable that fetch throws deep
    /// inside <c>HandleChallenge</c> and the user gets a bare 500.
    /// Fetching the document here first, through the same <see cref="IConfigurationManager{T}"/> the
    /// handler uses, turns an outage into an honest "login temporarily unavailable" page with a 503.
    /// When auth is up, the challenge below reuses the cached document and redirects exactly as
    /// before.
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
    /// Signs out of the cookie only, for cases where the session at the auth server is already gone, for
    /// example right after an account deletion was scheduled and the auth server locked the account and
    /// revoked its tokens.
    /// The normal <see cref="LogoutAsync"/> goes through the OIDC end-session endpoint and always ends on
    /// the registered post-logout URI, the home page. This one skips that pointless round trip and ends
    /// on the given local page instead, the "deletion scheduled" info page.
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
