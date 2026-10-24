using System.Globalization;
using LotroKoniecDev.Frontend.Infrastructure.Auth.SignOut;
using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Infrastructure.HttpClients;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Sets up this app as an OpenID Connect client: a cookie session as the default scheme, the sign-in
/// server with the authorization-code flow and PKCE, and <see cref="CookieTokenRefresher"/> on
/// <c>OnValidatePrincipal</c>.
/// </summary>
internal static partial class AuthenticationDependencyInjectionExtensions
{
    internal const string LoginPath = "/auth/login";
    internal const string LogoutPath = "/auth/logout";
    internal const string LocalSignOutPath = "/auth/local-signout";
    internal const string AccessDeniedPath = "/auth/access-denied";
    private const string ErrorPath = "/Error";

    /// <summary>
    /// A fixed local start with the "?" in it, so the return address can only ever land in the query.
    /// </summary>
    private const string LoginWithReturnUrl = LoginPath + "?returnUrl=";

    /// <summary>
    /// Per call to the sign-in server: the login renewal and each back-channel call (#923). A renewal that
    /// times out is a temporary failure while the access token still works (ADR-0067 §3). The limit must
    /// stay above the sign-in server's 20 seconds for opening a database connection, and each renewal is
    /// sent once, with no retry. The sign-in server's <c>AuthDatabaseConnectionString</c> explains both.
    /// </summary>
    internal static readonly TimeSpan AuthServerRequestTimeout = TimeSpan.FromSeconds(30);

    extension(IServiceCollection services)
    {
        public IServiceCollection AddFrontendAuthentication()
        {
            services.AddCascadingAuthenticationState();
            services.AddAuthorization();
            services.AddSingleton<SignInAvailability>();

            // Scoped: they hold a typed HttpClient, which a singleton would keep past the factory's
            // handler recycling.
            services.AddScoped<CookieTokenRefresher>();
            services.AddScoped<RefreshTokenRevoker>();
            services.TryAddSingleton(TimeProvider.System);

            // The refresh grant, the revoke and the OIDC back-channel below carry the visitor's address to
            // the auth API (ADR-0054). The revoke follows a link from the discovery document, so the origin
            // check comes first (#830).
            services.AddHttpContextAccessor();
            services.AddHttpClient<ITokenEndpointClient, TokenEndpointClient>((sp, client) =>
                {
                    AuthSystemSettings settings = sp
                        .GetRequiredService<IOptions<AuthSystemSettings>>().Value;
                    client.BaseAddress = new Uri(settings.BaseUrl);
                    client.Timeout = AuthServerRequestTimeout;
                })
                .AddSameOriginHandler<AuthSystemSettings>(settings => settings.BaseUrl)
                .AddFrontendCallerHandler<AuthSystemSettings>(settings => settings.CallerKey);

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    // A challenge goes to the login route, never straight to the sign-in server. Only the
                    // login route checks first that the discovery document loads, so an outage shows the
                    // "login temporarily unavailable" page and not the error page (#1153).
                    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    options.DefaultSignOutScheme = OpenIdConnectDefaults.AuthenticationScheme;
                })
                .AddCookie(options =>
                {
                    options.Cookie.Name = ".lotrokoniecdev.auth";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    // SecurePolicy is set per environment in ConfigureCookieSecurePolicy.
                    // A fixed path keeps the sign-in and sign-out cookies matched: a cookie written under
                    // a non-root PathBase could not be deleted by /auth/logout.
                    options.Cookie.Path = "/";
                    // This cookie carries the refresh token. The auth API's
                    // OpenIddict:RefreshTokenLifetimeMinutes must stay longer than this, with a margin
                    // (#1014).
                    options.ExpireTimeSpan = TimeSpan.FromHours(8);
                    options.SlidingExpiration = true;
                    options.LoginPath = LoginPath;
                    options.LogoutPath = LogoutPath;
                    options.AccessDeniedPath = AccessDeniedPath;
                    options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
                })
                .AddOpenIdConnect();

            services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
                .Configure<IHostEnvironment>(ConfigureCookieSecurePolicy);

            services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
                .Configure<IOptions<AuthSystemSettings>, IHttpContextAccessor>(ConfigureOpenIdConnect);

            return services;
        }
    }

    private static Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        CookieTokenRefresher refresher = context.HttpContext
            .RequestServices.GetRequiredService<CookieTokenRefresher>();
        return refresher.ValidateAsync(context);
    }

    /// <summary>
    /// Outside Development the cookie is always <c>Secure</c> (#400).
    /// <see cref="CookieSecurePolicy.SameAsRequest"/> would trust <c>X-Forwarded-Proto</c> behind the
    /// proxy, and one wrong scheme would send the cookie over plain HTTP. Development keeps it, so a local
    /// run over plain HTTP still works.
    /// </summary>
    private static void ConfigureCookieSecurePolicy(
        CookieAuthenticationOptions options,
        IHostEnvironment environment)
    {
        options.Cookie.SecurePolicy = environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    }

    private static void ConfigureOpenIdConnect(
        OpenIdConnectOptions options,
        IOptions<AuthSystemSettings> authSystemOptions,
        IHttpContextAccessor httpContextAccessor)
    {
        AuthSystemSettings settings = authSystemOptions.Value;

        options.Authority = settings.Authority;
        options.ClientId = settings.ClientId;
        options.RequireHttpsMetadata = !settings.Authority.StartsWith(
            "http://", StringComparison.OrdinalIgnoreCase);

        options.ResponseType = OpenIdConnectResponseType.Code;
        // Not form_post: its page submits itself with an inline script, which the auth origin's CSP
        // blocks, so sign-in would stop on a blank page (#693). It would also flash a white page. A code in
        // the URL is the RFC 9700 baseline, and PKCE makes a stolen code useless.
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

        // Without these an OIDC failure (sign-in server down, lost correlation cookie, refused consent)
        // becomes a 500.
        options.Events.OnRemoteFailure = OnRemoteFailureAsync;
        options.Events.OnAccessDenied = OnAccessDeniedAsync;
        options.Events.OnTokenResponseReceived = OnTokenResponseReceivedAsync;

        options.Events.OnTicketReceived = OnTicketReceivedAsync;

        // The handler's own back-channel client carries the visitor's address too (ADR-0054), and like the
        // typed clients it follows no redirect (#899) and keeps no cookies (#924). A handler set earlier is
        // wrapped, never replaced, with redirects and cookies switched off (#922); one set after this
        // Configure replaces the chain, which only tests do. An unknown charset name is dropped first (#972).
        HttpMessageHandler innerHandler =
            options.BackchannelHttpHandler ?? HttpClientsDependencyInjectionExtensions.CreatePrimaryHandler();
        HttpClientsDependencyInjectionExtensions.SwitchOffRedirectsAndCookies(innerHandler);
        options.BackchannelHttpHandler = new UnknownCharsetDelegatingHandler
        {
            InnerHandler = new FrontendCallerDelegatingHandler(httpContextAccessor, settings.CallerKey)
            {
                InnerHandler = innerHandler
            }
        };
        options.BackchannelTimeout = AuthServerRequestTimeout;
    }

    /// <summary>
    /// A failed sign-in while the sign-in server cannot be reached goes to the login route, which shows
    /// the "login temporarily unavailable" page; a reload there starts the sign-in again. This happens when
    /// the website restarted while the visitor was away and cannot load the discovery document (#1153).
    /// Every other failed sign-in ends on the error page.
    /// </summary>
    private static async Task OnRemoteFailureAsync(RemoteFailureContext context)
    {
        ILogger logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuthenticationDependencyInjectionExtensions).FullName!);
        LogOidcRemoteFailure(logger, context.Failure?.Message, context.Failure);

        SignInAvailability signInAvailability = context.HttpContext.RequestServices
            .GetRequiredService<SignInAvailability>();
        bool signInServerReachable = await signInAvailability.CanRedirectToSignInAsync(
            context.HttpContext.RequestAborted);

        context.Response.Redirect(signInServerReachable
            ? ErrorPath
            : LoginWithReturnUrl + Uri.EscapeDataString(context.Properties?.RedirectUri ?? "/"));
        context.HandleResponse();
    }

    /// <summary>
    /// The first sign-in holds the token answer to the rules the renewal uses (#1026). Otherwise the
    /// handler would store a blank access token, or no expiry, and the renewal would never run. A failure
    /// here goes through <see cref="OnRemoteFailureAsync"/>, which logs the reason.
    /// </summary>
    private static Task OnTokenResponseReceivedAsync(TokenResponseReceivedContext context)
    {
        OpenIdConnectMessage answer = context.TokenEndpointResponse;

        if (!TokenRules.IsUsable(answer.AccessToken))
        {
            context.Fail("The sign-in token answer has a missing, empty or blank access_token.");
            return Task.CompletedTask;
        }

        int? expiresInSeconds = TokenRules.ParseLifetime(answer.ExpiresIn);
        if (!TokenRules.IsPositiveLifetime(expiresInSeconds))
        {
            context.Fail(
                "The sign-in token answer has a missing, unreadable or non-positive expires_in. expires_in: "
                + (expiresInSeconds?.ToString(CultureInfo.InvariantCulture) ?? "missing or unreadable"));
            return Task.CompletedTask;
        }

        // The handler would store a blank refresh token. A missing one is allowed: OAuth makes it optional.
        if (!string.IsNullOrEmpty(answer.RefreshToken) && !TokenRules.IsUsable(answer.RefreshToken))
        {
            context.Fail("The sign-in token answer has a blank refresh_token.");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>SaveTokens</c> stores only <c>expires_at</c>, so the first token gets its refresh time here
    /// (#1025). This runs after the userinfo call, which only moves the refresh a little earlier. The clock
    /// is the one the handler used for <c>expires_at</c>.
    /// </summary>
    private static Task OnTicketReceivedAsync(TicketReceivedContext context)
    {
        if (context.Properties is { } properties
            && AccessTokenRefreshSchedule.TryGetMoment(
                properties, AccessTokenRefreshSchedule.ExpiresAtName, out DateTimeOffset expiresAt))
        {
            TimeProvider timeProvider = context.Options.TimeProvider ?? TimeProvider.System;
            AccessTokenRefreshSchedule.Schedule(properties, timeProvider.GetUtcNow(), expiresAt);
        }

        return Task.CompletedTask;
    }

    private static Task OnAccessDeniedAsync(AccessDeniedContext context)
    {
        ILogger logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(AuthenticationDependencyInjectionExtensions).FullName!);
        LogOidcAccessDenied(logger);

        context.Response.Redirect(AccessDeniedPath);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "OIDC remote failure: {FailureMessage}")]
    private static partial void LogOidcRemoteFailure(ILogger logger, string? failureMessage, Exception? exception);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "OIDC access denied by identity provider.")]
    private static partial void LogOidcAccessDenied(ILogger logger);
}
