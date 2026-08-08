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

// Deliberately NOT CreateBootstrapLogger(): the reloadable bootstrap logger lives in the shared
// static Log.Logger slot, and AddSerilog freezes it on the host's first logger resolution. The
// integration suite boots several hosts from this Program concurrently (the shared factory plus a
// brokered factory per pipeline suite), so one host can freeze the bootstrap logger another host
// just installed — the second freeze then dies with "The logger is already frozen". A plain
// console logger keeps startup logging (and the catch/finally below) working while AddSerilog
// builds each host its own fully-configured pipeline; in production nothing changes — the single
// host's final logger still replaces this one.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    Log.Information("Starting AuthSystem");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // DI validation in EVERY environment, not just Development (#572): a captive dependency or an
    // unresolvable constructor that only manifests under Production config fails the container at
    // startup — where CD smokes the 0%-traffic candidate — instead of surfacing as a 500 on first
    // hit. Registered-services-only: a forgotten closed handler registration is still resolved at
    // request time, so endpoint integration tests remain the guard for that.
    builder.Host.UseDefaultServiceProvider(static (_, options) =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });

    // Optional, git-ignored per-developer overrides (e.g. AdminUser:* seed credentials), the same
    // machine-local file the EF design-time factories already read. It survives `docker compose
    // down -v`, so a local admin is re-seeded on the next host start.
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

    // Configured before the host is built and read straight from configuration (M6-04, mirrors the
    // Frontend posture in ADR-0005): the keyring must be persistent and the application name pinned
    // so the Identity login cookie, Razor antiforgery, and password-reset/email-confirmation tokens
    // survive restarts and are shared across replicas. Registered before AddAuthentication, which
    // depends on the configured keyring to protect its cookie.
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

        // Revalidate the Identity security stamp on every request so a password reset/change/delete
        // (which rotates the stamp) evicts a still-live auth cookie before it can mint fresh tokens
        // via /connect/authorize (SEC-03, #282).
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

    // Wire the OTLP exporter only when an endpoint is configured; otherwise the SDK keeps retrying
    // against the default localhost:4317 collector, which does not exist in the cloud runtime.
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

    // Behind the reverse proxy TLS terminates at the proxy and the container receives plain HTTP
    // with X-Forwarded-* headers. Honour them so Request.Scheme is https — OAuth redirects,
    // scheme-derived HATEOAS hrefs, Secure cookies, and UseHttpsRedirection all depend on it. (The
    // OpenIddict token/discovery `iss` is NOT scheme-derived: it is pinned from
    // OpenIddictSettings.Issuer, so it stays correct regardless of these headers.)
    //
    // Trust policy (#399): ForwardedHeaders:KnownNetworks restricts trust to the proxy's CIDR, and
    // every deployed stack pins it — compose.hetzner.yaml (the Hetzner boxes) and compose.prod.yaml
    // (the local parity stack) both set the Caddy network. An empty list trusts EVERY upstream; that
    // fallback is safe only under the recorded invariant that the container port is never published
    // directly (both stacks use expose:, not ports: — Caddy is the sole route), and since the move
    // off ACA (ADR-0034) no environment relies on it.
    // ForwardLimit = 1 is explicit either way: exactly one proxy hop sets these headers, so only
    // the right-most X-Forwarded-* entry (the ingress-observed client) is ever applied. A malformed
    // CIDR — or a knob that is set yet yields no entries (e.g. a scalar value missing the __0
    // index) — aborts boot here (fail-fast, ADR-0008 §3 spirit), never silently widens trust.
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

    // CORS origins are environment-injected, not baked into code (ADR-0008 §3, M6-03): the production
    // policy admits only the configured browser origins, validated at startup so a missing or
    // malformed origin aborts boot in Staging/Production (CorsSettingsValidator) instead of silently
    // blocking the browser. Development uses the permissive policy below and ignores this list.
    // AllowCredentials() (required for the cookie/OIDC auth flows) is preserved — it pairs with the
    // explicit WithOrigins list, never with AllowAnyOrigin (which the framework would reject).
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
        // The db check is deliberately NOT tagged "ready": ACA probes /health/ready every few
        // seconds, and a DB ping there keeps the scale-to-zero Neon compute awake 24/7 (a suspended
        // database is normal operation, not unreadiness — ADR-0025). The check stays reachable on
        // demand via the full /health; deploys prove the DB through the smoke's real endpoints.
        .AddNpgSql(
            connectionStringFactory: sp => sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.AuthDatabase,
            name: "authdb",
            tags: ["db", "postgres"])
        // SMTP is likewise NOT tagged "ready": a Brevo outage must not pull the whole auth service
        // out of the ingress rotation — login and token issuance work without mail. Mail failures
        // surface in logs, via "resend confirmation", and on the full /health.
        .AddCheck<SmtpHealthCheck>(
            "smtp",
            tags: ["smtp"])
        // The broker is likewise NOT tagged "ready": e-mail messaging degrades gracefully while it
        // is down (outbox rows wait, the consumer reconnects with backoff), and login/token
        // issuance don't need it. A down broker container surfaces on the full /health the daily
        // health ping probes.
        .AddCheck<RabbitMqHealthCheck>(
            "rabbitmq",
            tags: ["broker"]);

    const string rateLimitPolicy = "fixed-by-ip";
    const string authEndpointRateLimitPolicy = "auth-endpoint-limit";
    const string forgotPasswordRateLimitPolicy = "forgot-password-limit";
    const string resendConfirmationRateLimitPolicy = "resend-confirmation-limit";
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

        // Stricter rate limiting for auth endpoints to prevent brute force attacks
        options.AddPolicy(authEndpointRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // Very strict rate limiting for forgot-password to prevent email bombing
        options.AddPolicy(forgotPasswordRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromMinutes(15)
                }));

        // Separate rate limiting for email confirmation resend. The budget belongs to the SEND: the
        // policy rides an [EnableRateLimiting] attribute on the Razor PageModel, and a Razor Page is
        // one endpoint for both verbs, so a verb-blind partition spends the 3-per-15-minutes window on
        // page views — three of them and the user cannot even reach the form. That is the form
        // ADR-0046 made the advertised one-click fix for a blocked login, so the distinction is
        // load-bearing, not cosmetic. Rendering a form costs nothing; sending mail is what needs the cap.
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

    // Must run before anything that reads the request scheme/host (logging, HSTS, redirect, auth,
    // OpenIddict endpoint resolution). Skipped in Development so `docker compose up` keeps its
    // plain-http behaviour unchanged; active in Testing + Production/Staging where a TLS-terminating
    // proxy sets X-Forwarded-Proto. With the proto honoured first, UseHttpsRedirection below is a
    // no-op (the scheme already reads https) — there is no redirect loop.
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    app.UseRequestContextLogging();

    app.UseSerilogRequestLogging(options =>
    {
        // Redact the request log (audit #0001 / M5): keep RequestPath query-free and log the query
        // separately with secrets stripped and e-mails masked, so no OAuth code/token or PII is persisted.
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

    // Only Development gets the permissive AllowAnyOrigin policy; every deployed environment
    // (Staging + Production, ADR-0008 §1) serves the restrictive configured-origins policy, so a
    // staging origin is never silently waved through.
    string corsPolicy = app.Environment.IsDevelopment()
        ? localCorsPolicy
        : productionCorsPolicy;
    app.UseCors(corsPolicy);

    // BEFORE authentication: OpenIddict validates /connect/* requests inside the authentication
    // stage and short-circuits invalid ones (e.g. unknown client_id floods) — a limiter placed
    // after it would never count exactly the junk traffic it exists to stop. Routing has already
    // selected the endpoint here, so the per-endpoint policy metadata is visible to the limiter.
    // Off in Development/Testing so local flows and the test suites never trip the limits;
    // RateLimiting:ForceEnable lets a test host arm the middleware to observe real 429 rejection.
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

    // Policy metadata is attached unconditionally (matching the per-endpoint RequireRateLimiting
    // calls in the feature slices); UseRateLimiter above is the single switch deciding enforcement.
    RouteGroupBuilder endpointsGroup = app.MapGroup("")
        .RequireRateLimiting(rateLimitPolicy);

    app.MapApiEndpoints(endpointsGroup);

    // OpenIddict's /connect/* endpoints live at the root and are mapped THROUGH this group — a
    // group convention binds only to endpoints mapped through it, so this is what actually arms
    // the brute-force limiter on /connect/token (a bare MapGroup("/connect") never engaged it).
    RouteGroupBuilder rootEndpointsGroup = app.MapGroup("")
        .RequireRateLimiting(authEndpointRateLimitPolicy);

    app.MapRootEndpoints(rootEndpointsGroup);

    app.MapRazorPages();

    // Serves the self-hosted web fonts referenced by the hosted account pages (LEGAL-06); sets its
    // own Cache-Control (ETag revalidation), so GlobalNoCacheMiddleware leaves these responses alone.
    app.MapStaticAssets();

    // Seed is idempotent - checks for existing data before inserting.
    // In integration tests, the seed may be called twice (here and in test setup) which is safe.
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
