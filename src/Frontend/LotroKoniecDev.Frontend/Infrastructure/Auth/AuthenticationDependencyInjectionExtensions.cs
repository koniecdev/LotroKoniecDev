using LotroKoniecDev.Frontend.Infrastructure.Auth.TokenRefresh;
using LotroKoniecDev.Frontend.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LotroKoniecDev.Frontend.Infrastructure.Auth;

/// <summary>
/// Sets up this app as an OpenID Connect client: a cookie session as the default scheme, the OpenIddict
/// AuthSystem for login and logout with the authorization-code flow and PKCE, and
/// <see cref="CookieTokenRefresher"/> on <c>OnValidatePrincipal</c>.
/// The login and logout UI, the rules for which pages need a login, and the user info in the navbar come
/// with the M3-02 slice. This file only puts the client infrastructure in place, so that slice just
/// switches it on.
/// </summary>
internal static class AuthenticationDependencyInjectionExtensions
{
    internal const string LoginPath = "/auth/login";
    internal const string LogoutPath = "/auth/logout";
    internal const string LocalSignOutPath = "/auth/local-signout";
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

            // Scoped and not singleton, because ITokenEndpointClient wraps an HttpClient that
            // IHttpClientFactory provides. Holding a short-lived typed client inside a singleton would
            // break how the factory recycles connections and picks up DNS changes.
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
                    // SecurePolicy depends on the environment and is set in ConfigureCookieSecurePolicy
                    // below.
                    // The path is fixed so the sign-in and sign-out cookies always match. Without it, a
                    // future code path that signs in under a non-root PathBase would write a cookie that
                    // /auth/logout, which runs with an empty PathBase, could not delete.
                    options.Cookie.Path = "/";
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

    /// <summary>
    /// AUDIT-SEC-10 (#400): outside Development the session cookie is always <c>Secure</c>.
    /// <see cref="CookieSecurePolicy.SameAsRequest"/> would tie that flag to <c>Request.Scheme</c>, which
    /// behind the TLS-terminating proxy comes from <c>X-Forwarded-Proto</c>. One wrong scheme would write
    /// the cookie without <c>Secure</c> and let the browser send it over plain HTTP.
    /// Development keeps <see cref="CookieSecurePolicy.SameAsRequest"/>, so a local run over plain HTTP
    /// still produces a cookie the browser accepts.
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
        IOptions<AuthSystemSettings> authSystemOptions)
    {
        AuthSystemSettings settings = authSystemOptions.Value;

        options.Authority = settings.Authority;
        options.ClientId = settings.ClientId;
        options.RequireHttpsMetadata = !settings.Authority.StartsWith(
            "http://", StringComparison.OrdinalIgnoreCase);

        options.ResponseType = OpenIdConnectResponseType.Code;
        // Not the handler's form_post default. With form_post the authorize endpoint renders OpenIddict's
        // plain white page, which flashes before the dark app repaints. With query the whole login return
        // is a chain of redirects the browser never draws.
        // Putting the code in the URL is the baseline shape RFC 9700 describes, and PKCE below is what
        // makes a stolen code useless.
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

        // Without these handlers an OIDC failure, such as the AuthSystem being down, a lost correlation
        // cookie or a user who refused consent, becomes a 500 and the user lands on the generic /Error
        // page with nothing useful. Redirecting to our own pages keeps the trace id working, because
        // UseExceptionHandler and UseStatusCodePages run again with the new path.
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
