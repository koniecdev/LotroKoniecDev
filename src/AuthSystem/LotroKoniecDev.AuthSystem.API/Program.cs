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
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Persistence.Settings;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting AuthSystem");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // Optional, git-ignored per-developer overrides (e.g. AdminUser:* seed credentials), the same
    // machine-local file the EF design-time factories already read. It survives `docker compose
    // down -v`, so a local admin is re-seeded on the next host start.
    builder.Configuration.AddJsonFile(
        "appsettings.Local.json",
        optional: true,
        reloadOnChange: true);

    builder.Services.AddSerilog((services, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
          .ReadFrom.Services(services);

        string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
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
    });

    builder.Services.AddAuthorization();
    builder.Services.AddRazorPages();

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddOpenApi();

    builder.Services.AddOpenTelemetry()
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
            .AddMeter("Npgsql"))
        .UseOtlpExporter();
    
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
        options.MimeTypes = ResponseCompressionDefaults.MimeTypes;
    });

    // Behind a cloud ingress (ACA / ALB / any reverse proxy) TLS terminates at the proxy and the
    // container receives plain HTTP with X-Forwarded-* headers. Honour them so Request.Scheme is
    // https — OAuth redirects, scheme-derived HATEOAS hrefs, Secure cookies, and UseHttpsRedirection
    // all depend on it. (The OpenIddict token/discovery `iss` is NOT scheme-derived: it is pinned
    // from OpenIddictSettings.Issuer, so it stays correct regardless of these headers.) The ingress
    // hop has no stable IP, so KnownIPNetworks/KnownProxies are cleared (every upstream proxy is
    // trusted), which is safe only because the container is never exposed directly: it is always
    // reached through the ingress that sets these headers (ADR-0008).
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                   | ForwardedHeaders.XForwardedProto
                                   | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
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
        .AddNpgSql(
            connectionStringFactory: sp => sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.AuthDatabase,
            name: "authdb",
            tags: ["db", "postgres", "ready"])
        .AddCheck<SmtpHealthCheck>(
            "smtp",
            tags: ["smtp", "ready"]);

    const string rateLimitPolicy = "fixed-by-ip";
    const string authEndpointRateLimitPolicy = "auth-endpoint-limit";
    const string forgotPasswordRateLimitPolicy = "forgot-password-limit";
    const string resendConfirmationRateLimitPolicy = "resend-confirmation-limit";

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

        // Separate rate limiting for email confirmation resend
        options.AddPolicy(resendConfirmationRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 3,
                    Window = TimeSpan.FromMinutes(15)
                }));
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
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
        {
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

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseAuthorizationLogging();

    if (!app.Environment.IsDevelopment() && !app.Environment.IsTesting())
    {
        app.UseRateLimiter();
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.MapScalarApiReference();
    }

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

    RouteGroupBuilder endpointsGroup = app.MapGroup("");

    if (!app.Environment.IsDevelopment() && !app.Environment.IsTesting())
    {
        endpointsGroup.RequireRateLimiting(rateLimitPolicy);
    }

    app.MapApiEndpoints(endpointsGroup);

    // OpenIddict endpoints are mapped directly at the root (e.g. /connect/token).
    // Apply rate limiting to auth endpoints to prevent brute force attacks.
    if (!app.Environment.IsDevelopment() && !app.Environment.IsTesting())
    {
        app.MapGroup("/connect")
            .RequireRateLimiting(authEndpointRateLimitPolicy);
    }

    app.MapRootEndpoints();

    app.MapRazorPages();

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
