using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Sinks.OpenTelemetry;
using LotroKoniecDev.AuthSystem.API;
using LotroKoniecDev.AuthSystem.API.Extensions;
using LotroKoniecDev.AuthSystem.API.Health;
using LotroKoniecDev.AuthSystem.API.Middleware;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Persistence.Settings;
using LotroKoniecDev.Logging.Redaction;

// Not CreateBootstrapLogger(), on purpose. That logger lives in the shared static Log.Logger slot,
// and AddSerilog freezes it the first time a host resolves a logger. The integration tests start
// several hosts from this Program at the same time, the shared factory plus one broker factory per
// pipeline suite, so one host can freeze the bootstrap logger another host has just installed, and the
// second freeze fails with "The logger is already frozen".
// A plain console logger keeps startup logging, and the catch and finally below, working while
// AddSerilog builds each host its own pipeline. Nothing changes in production: the single host's final
// logger still replaces this one.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    Log.Information("Starting AuthSystem");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // DI validation runs in every environment, not only in Development (#572). A captive dependency or
    // a constructor that cannot be resolved, even one that only appears under production config, then
    // fails at startup, where CD smoke-tests the candidate before it takes traffic, instead of turning
    // into a 500 on the first request.
    // It only checks registered services. A forgotten closed handler registration is still resolved at
    // request time, so endpoint integration tests stay the guard for that.
    builder.Host.UseDefaultServiceProvider(static (_, options) =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });

    // Optional per-developer overrides, such as the AdminUser:* seed credentials. The file is
    // git-ignored and is the same one the EF design-time factories already read. It survives
    // `docker compose down -v`, so a local admin is created again on the next start.
    builder.Configuration.AddJsonFile(
        "appsettings.Local.json",
        optional: true,
        reloadOnChange: true);

    string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

    builder.Services.AddSerilog((services, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
          .ReadFrom.Services(services);

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            lc.WriteTo.OpenTelemetry(opts =>
            {
                opts.Endpoint = otlpEndpoint;
                opts.Protocol = string.Equals(
                    builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"],
                    "http/protobuf",
                    StringComparison.OrdinalIgnoreCase)
                    ? OtlpProtocol.HttpProtobuf
                    : OtlpProtocol.Grpc;
                opts.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = builder.Environment.ApplicationName
                };
            });
        }
    });

    builder.Services.AddAuthSystem(builder.Environment);

    // Set up before the host is built, straight from configuration (M6-04, the same approach the
    // Frontend takes in ADR-0005). The keyring has to be kept and the application name has to stay the
    // same, so the Identity login cookie, the Razor antiforgery tokens and the password-reset and
    // e-mail-confirmation tokens survive a restart and work across replicas.
    // It is registered before AddAuthentication, which needs the keyring to protect its cookie.
    builder.Services.AddAuthDataProtection(builder.Configuration, builder.Environment);

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
    })
    .AddCookie(Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme, options =>
    {
        options.LoginPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;
        options.Cookie.Name = "LotroKoniecDev.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

        // Check the Identity security stamp on every request, so a password reset, change or delete,
        // which all change the stamp, drops an auth cookie that is still valid before it can get fresh
        // tokens from /connect/authorize (SEC-03, #282).
        options.Events.OnValidatePrincipal = SecurityStampCookieValidator.ValidatePrincipalAsync;
    });

    builder.Services.AddAuthorization();
    builder.Services.AddRazorPages();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();

    IOpenTelemetryBuilder openTelemetryBuilder = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
        .WithTracing(tracing => tracing
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddSource("Npgsql"))
        .WithMetrics(metrics => metrics
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter("Microsoft.EntityFrameworkCore")
            .AddMeter("Npgsql"));

    // Add the OTLP exporter only when an endpoint is configured. Otherwise the SDK keeps retrying
    // against the default localhost:4317 collector, which does not exist in the cloud.
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        openTelemetryBuilder.UseOtlpExporter();
    }

    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
    });

    // Behind the reverse proxy, TLS ends at the proxy and the container gets plain HTTP with
    // X-Forwarded-* headers. We read them so Request.Scheme is https, which OAuth redirects, HATEOAS
    // hrefs, Secure cookies and UseHttpsRedirection all depend on. The `iss` value in OpenIddict tokens
    // and discovery does not come from the scheme: it is fixed in OpenIddictSettings.Issuer and stays
    // correct whatever these headers say.
    //
    // Who we trust (#399): ForwardedHeaders:KnownNetworks limits trust to the proxy's network, and
    // every deployed stack sets it. compose.hetzner.yaml for the Hetzner boxes and compose.prod.yaml
    // for the local parity stack both point at the Caddy network. An empty list would trust every
    // upstream. That is only safe because the container port is never published directly: both stacks
    // use expose: and not ports:, so Caddy is the only way in. Since the move off ACA (ADR-0034) no
    // environment relies on that fallback.
    // ForwardLimit = 1 is written out either way: exactly one proxy sets these headers, so only the
    // right-most X-Forwarded-* entry, the client the ingress saw, is used. A malformed network, or a
    // setting that exists but produces no entries, for example a scalar value missing the __0 index,
    // stops the boot here instead of quietly trusting more (ADR-0008 §3).
    IConfigurationSection trustedProxyNetworksSection =
        builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks");
    System.Net.IPNetwork[] trustedProxyNetworks = (trustedProxyNetworksSection.Get<string[]>() ?? [])
        .Select(cidr => System.Net.IPNetwork.Parse(cidr))
        .ToArray();
    if (trustedProxyNetworksSection.Exists() && trustedProxyNetworks.Length == 0)
    {
        throw new InvalidOperationException(
            "ForwardedHeaders:KnownNetworks is set but yielded no networks - "
            + "expected indexed CIDR values (ForwardedHeaders__KnownNetworks__0).");
    }

    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                   | ForwardedHeaders.XForwardedProto
                                   | ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (System.Net.IPNetwork trustedProxyNetwork in trustedProxyNetworks)
        {
            options.KnownIPNetworks.Add(trustedProxyNetwork);
        }
    });

    builder.Services.Configure<RouteHandlerOptions>(options =>
    {
        options.ThrowOnBadRequest = true;
    });

    // The CORS origins come from the environment and are not written into the code (ADR-0008 §3,
    // M6-03). The production policy allows only the configured browser origins, and they are checked at
    // startup, so a missing or malformed origin stops the boot in Staging and Production
    // (CorsSettingsValidator) instead of quietly blocking the browser. Development uses the open policy
    // below and ignores this list.
    // AllowCredentials(), which the cookie and OIDC flows need, stays. It works together with the
    // explicit WithOrigins list and never with AllowAnyOrigin, which the framework would refuse.
    builder.Services.AddOptions<CorsSettings>()
        .BindConfiguration(CorsSettings.ConfigurationSection)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<CorsSettings>, CorsSettingsValidator>();

    string[] allowedCorsOrigins = builder.Configuration
        .GetSection(CorsSettings.ConfigurationSection)
        .Get<CorsSettings>()?.AllowedOrigins ?? [];

    const string localCorsPolicy = "DevelopmentPolicy";
    const string productionCorsPolicy = "ProductionPolicy";

    builder.Services.AddCors(options =>
    {
        options.AddPolicy(localCorsPolicy, corsBuilder =>
        {
            corsBuilder
                .AllowAnyOrigin()
                .AllowAnyHeader()
                .AllowAnyMethod();
        });
        options.AddPolicy(productionCorsPolicy, corsBuilder =>
        {
            corsBuilder
                .WithOrigins(allowedCorsOrigins)
                .WithHeaders("Authorization", "Content-Type", "X-Requested-With", "Accept")
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
                .AllowCredentials();
        });
    });

    builder.Services
        .AddHealthChecks()
        // The database check is not tagged "ready" on purpose. The platform probes /health/ready every
        // few seconds, and a database ping there would keep the Neon compute awake all day. A suspended
        // database is normal operation and not a sign that the app is not ready (ADR-0025). The check
        // is still available on the full /health, and a deploy proves the database through the smoke
        // test's real endpoints.
        .AddNpgSql(
            connectionStringFactory: sp => sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.AuthDatabase,
            name: "authdb",
            tags: ["db", "postgres"])
        // SMTP is not tagged "ready" either. A Brevo outage must not take the auth service out of the
        // load balancer, because login and token issuance work without mail. Mail problems show up in
        // the logs, through "resend confirmation", and on the full /health.
        .AddCheck<SmtpHealthCheck>(
            "smtp",
            tags: ["smtp"])
        // The broker is not tagged "ready" either. While it is down, e-mail simply waits: outbox rows
        // stay and the consumer reconnects. Login and token issuance do not need it. A broker that is
        // down shows up on the full /health, which the daily health ping reads.
        .AddCheck<RabbitMqHealthCheck>(
            "rabbitmq",
            tags: ["broker"]);

    const string rateLimitPolicy = "fixed-by-ip";
    const string authEndpointRateLimitPolicy = "auth-endpoint-limit";
    const string forgotPasswordRateLimitPolicy = "forgot-password-limit";
    const string resendConfirmationRateLimitPolicy = "resend-confirmation-limit";
    const string changeEmailRateLimitPolicy = "change-email-limit";
    const string resendConfirmationPageViewPartition = "resend-confirmation-page-view";

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy(rateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // A stricter rate limit on the auth endpoints, against brute-force attacks.
        options.AddPolicy(authEndpointRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // A very strict rate limit on forgot-password, so nobody can flood an inbox.
        options.AddPolicy(forgotPasswordRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromMinutes(15)
                }));

        // The e-mail change request sends mail to an address the caller typed in, so it can be used to
        // flood a stranger's inbox. Same threat as forgot-password, same budget.
        // The key is the IP and not the user: UseRateLimiter runs before UseAuthentication on purpose
        // (see below), so httpContext.User is still anonymous here and a "per user" key would collapse
        // into one bucket shared by everybody.
        options.AddPolicy(changeEmailRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromHours(1)
                }));

        // A separate rate limit for resending the confirmation e-mail. The limit belongs to the send,
        // not to the page. The policy sits on an [EnableRateLimiting] attribute on the Razor PageModel,
        // and a Razor Page is one endpoint for both GET and POST. A limit that ignored the verb would
        // spend its 3-per-15-minutes budget on page views, and after three views the user could not
        // even reach the form. ADR-0046 made that form the one-click fix we advertise for a blocked
        // login, so the difference matters. Showing a form costs nothing; sending mail is what needs
        // the limit.
        options.AddPolicy(resendConfirmationRateLimitPolicy, httpContext =>
            HttpMethods.IsPost(httpContext.Request.Method)
                ? RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(15)
                    })
                : RateLimitPartition.GetNoLimiter<string>(resendConfirmationPageViewPartition));
    });

    WebApplication app = builder.Build();

    // This has to run before anything that reads the request scheme or host: logging, HSTS, the
    // redirect, authentication and OpenIddict's endpoint resolution. It is skipped in Development so
    // `docker compose up` keeps working over plain http, and it is on in Testing, Staging and
    // Production, where a proxy terminates TLS and sets X-Forwarded-Proto. Because the scheme is read
    // first, UseHttpsRedirection below does nothing and there is no redirect loop.
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    app.UseRequestContextLogging();

    app.UseSerilogRequestLogging(options =>
    {
        // Clean the request log (audit #0001, M5): keep the query string out of RequestPath and log it
        // separately with secrets removed and e-mails masked, so no OAuth code, token or personal data
        // is stored.
        options.IncludeQueryInRequestPath = false;
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath}{RequestQuery} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
            diagnosticContext.Set(
                "RequestQuery",
                SensitiveDataRedactor.RedactQueryString(httpContext.Request.QueryString.Value));

            if (httpContext.Items.TryGetValue("CorrelationId", out object? correlationId))
            {
                diagnosticContext.Set("CorrelationId", correlationId);
            }
        };
    });

    app.UseExceptionHandler();
    app.UseStatusCodePages();
    if (!app.Environment.IsDevelopment() && !app.Environment.IsTesting())
    {
        app.UseHsts();
        app.UseHttpsRedirection();
    }
    app.UseResponseCompression();
    app.UseRouting();
    app.UseGlobalNoCache();

    // Only Development gets the open AllowAnyOrigin policy. Every deployed environment, Staging and
    // Production (ADR-0008 §1), uses the configured-origins policy, so a staging origin is never let
    // through by accident.
    string corsPolicy = app.Environment.IsDevelopment()
        ? localCorsPolicy
        : productionCorsPolicy;
    app.UseCors(corsPolicy);

    // This runs before authentication. OpenIddict checks /connect/* requests during authentication and
    // stops the invalid ones early, for example a flood with an unknown client_id, so a limiter placed
    // after it would never count the junk traffic it exists to stop. Routing has already picked the
    // endpoint here, so the limiter can read the per-endpoint policy.
    // It is off in Development and Testing, so local flows and the test suites never hit the limits.
    // RateLimiting:ForceEnable lets a test host turn it on to see a real 429.
    bool rateLimiterOffByEnvironment = app.Environment.IsDevelopment() || app.Environment.IsTesting();
    if (!rateLimiterOffByEnvironment || app.Configuration.GetValue<bool>("RateLimiting:ForceEnable"))
    {
        app.UseRateLimiter();
    }

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseAuthorizationLogging();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    });

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    });

    // The policy metadata is always attached, like the per-endpoint RequireRateLimiting calls in the
    // feature slices. UseRateLimiter above is the one switch that decides whether it is enforced.
    RouteGroupBuilder endpointsGroup = app.MapGroup("")
        .RequireRateLimiting(rateLimitPolicy);

    app.MapApiEndpoints(endpointsGroup);

    // OpenIddict's /connect/* endpoints live at the root and are mapped through this group. A group
    // convention only reaches the endpoints mapped through it, so this is what really turns the
    // brute-force limiter on for /connect/token. A plain MapGroup("/connect") never did.
    RouteGroupBuilder rootEndpointsGroup = app.MapGroup("")
        .RequireRateLimiting(authEndpointRateLimitPolicy);

    app.MapRootEndpoints(rootEndpointsGroup);

    app.MapRazorPages();

    // Serves the web fonts the account pages use, which we host ourselves (LEGAL-06). It sets its own
    // Cache-Control with ETag revalidation, so GlobalNoCacheMiddleware leaves these responses alone.
    app.MapStaticAssets();

    // The seed checks for existing rows before it inserts, so running it twice is safe. Integration
    // tests do call it twice, here and in their own setup.
    await app.SeedAuthDatabaseAsync();

    await app.RunAsync();
}
#pragma warning disable S2139
catch (Exception ex)
{
    Log.Fatal(ex, "AuthSystem terminated unexpectedly");
    throw;
}
#pragma warning restore S2139
finally
{
    await Log.CloseAndFlushAsync();
}
