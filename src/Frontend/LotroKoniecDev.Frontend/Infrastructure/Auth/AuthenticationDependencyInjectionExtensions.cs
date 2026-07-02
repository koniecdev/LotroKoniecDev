using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Wires the OpenID Connect relying-party: a cookie session as the default scheme, the OpenIddict
/// AuthSystem as the challenge/sign-out scheme (authorization-code + PKCE), and
/// <see cref="CookieTokenRefresher"/> on <c>OnValidatePrincipal</c>. The interactive login/logout UX,
/// protected-vs-public page policy, and navbar user info land in the M3-02 auth-session slice; this
/// lifts the RP infrastructure so that slice is pure activation.
/// </summary>
internal static class AuthenticationDependencyInjectionExtensions
{
    internal const string LoginPath = "/auth/login";
    internal const string LogoutPath = "/auth/logout";
    internal const string AccessDeniedPath = "/auth/access-denied";
    private const string ErrorPath = "/Error";

    private static readonly Action<ILogger, string?, Exception?> LogOidcRemoteFailure =
        LoggerMessage.Define<string?>(
            LogLevel.Warning,
            new EventId(1, nameof(LogOidcRemoteFailure)),
            "OIDC remote failure: {FailureMessage}");

    private static readonly Action<ILogger, Exception?> LogOidcAccessDenied =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(2, nameof(LogOidcAccessDenied)),
            "OIDC access denied by identity provider.");

    extension(IServiceCollection services)
    {
        public IServiceCollection AddFrontendAuthentication()
        {
            services.AddCascadingAuthenticationState();
            services.AddAuthorization();

            // Scoped, not singleton — ITokenEndpointClient wraps an HttpClient supplied by
            // IHttpClientFactory. Capturing a transient typed client inside a singleton would defeat the
            // factory's connection lifecycle and DNS-refresh story.
            services.AddScoped<CookieTokenRefresher>();
            services.AddHttpClient<ITokenEndpointClient, TokenEndpointClient>((sp, client) =>
            {
                AuthSystemSettings settings = sp
                    .GetRequiredService<IOptions<AuthSystemSettings>>().Value;
                client.BaseAddress = new Uri(settings.BaseUrl);
            });

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
                    options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie(options =>
                {
                    options.Cookie.Name = ".lotrokoniecdev.auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    // Lock path so SignIn/SignOut cookies always match — without this, a future code path
                    // that signs in under a non-root PathBase would produce a cookie that /auth/logout
                    // (PathBase="") cannot delete.
                    options.Cookie.Path = "/";
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = true;
                    options.LoginPath = LoginPath;
                    options.LogoutPath = LogoutPath;
                    options.AccessDeniedPath = AccessDeniedPath;
                    options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
                })
                .AddOpenIdConnect();

            services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
                .Configure<IOptions<AuthSystemSettings>>(ConfigureOpenIdConnect);

            return services;
        }
    }

    private static Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        CookieTokenRefresher refresher = context.HttpContext
            .RequestServices.GetRequiredService<CookieTokenRefresher>();
        return refresher.ValidateAsync(context);
    }

    private static void ConfigureOpenIdConnect(
        OpenIdConnectOptions options,
        IOptions<AuthSystemSettings> authSystemOptions)
    {
        AuthSystemSettings settings = authSystemOptions.Value;

        options.Authority = settings.Authority;
        options.ClientId = settings.ClientId;
        options.RequireHttpsMetadata = !settings.Authority.StartsWith(
            "http://", StringComparison.OrdinalIgnoreCase);

        options.ResponseType = OpenIdConnectResponseType.Code;
        // Not the handler's form_post default: form_post makes the authorize endpoint render
        // OpenIddict's bare white interstitial (visible flash before a dark app repaints), while query
        // keeps the whole login return a 302 chain the browser never paints. Code-in-URL is the
        // RFC 9700 baseline shape — PKCE below is the mitigation that makes an intercepted code useless.
        options.ResponseMode = OpenIdConnectResponseMode.Query;
        options.UsePkce = true;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.MapInboundClaims = false;

        options.CallbackPath = settings.CallbackPath;
        options.SignedOutCallbackPath = settings.SignedOutCallbackPath;
        options.SignedOutRedirectUri = "/";

        options.Scope.Clear();
        foreach (string scope in settings.Scopes)
        {
            options.Scope.Add(scope);
        }

        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";

        // Without these handlers, OIDC remote failures (AuthSystem down, correlation cookie lost, user
        // denied consent) propagate as 500s and the user lands on the generic /Error page with no
        // actionable context. Redirecting to dedicated pages keeps the trace ID flow intact
        // (UseExceptionHandler/UseStatusCodePages re-execute with the rewritten path).
        options.Events.OnRemoteFailure = OnRemoteFailureAsync;
        options.Events.OnAccessDenied = OnAccessDeniedAsync;
    }

    private static Task OnRemoteFailureAsync(RemoteFailureContext context)
    {
        ILogger logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuthenticationDependencyInjectionExtensions).FullName!);
        LogOidcRemoteFailure(logger, context.Failure?.Message, context.Failure);

        context.Response.Redirect(ErrorPath);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    private static Task OnAccessDeniedAsync(AccessDeniedContext context)
    {
        ILogger logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuthenticationDependencyInjectionExtensions).FullName!);
        LogOidcAccessDenied(logger, null);

        context.Response.Redirect(AccessDeniedPath);
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
