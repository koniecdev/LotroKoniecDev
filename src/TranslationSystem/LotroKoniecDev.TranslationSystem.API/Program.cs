using System.Globalization;
using System.Reflection;
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
using LotroKoniecDev.TranslationSystem.API;
using LotroKoniecDev.TranslationSystem.API.Extensions;
using LotroKoniecDev.TranslationSystem.API.Health;
using LotroKoniecDev.TranslationSystem.API.Middleware;
using LotroKoniecDev.TranslationSystem.API.Settings;
using LotroKoniecDev.TranslationSystem.Persistence.Settings;
using LotroKoniecDev.Logging.Redaction;

// Not CreateBootstrapLogger(), on purpose. That logger lives in the shared static Log.Logger slot, and
// AddSerilog freezes it the first time a host resolves a logger. Any test run that starts a second host
// from this Program, such as a WithWebHostBuilder child or a second class fixture, lets one host freeze
// the bootstrap logger another host has just installed, and the second freeze fails with "The logger is
// already frozen".
// A plain console logger keeps startup logging, and the catch and finally below, working while
// AddSerilog builds each host its own pipeline. Nothing changes in production: the single host's final
// logger still replaces this one.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    Log.Information("Starting TranslationSystem");

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

    builder.Services.AddTranslationSystem();
    builder.Services.AddJwtBearerAuthentication(builder.Environment);

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
    // X-Forwarded-* headers. We read them so Request.Scheme is https, which keeps the JWT issuer, the
    // HATEOAS hrefs, Secure cookies and UseHttpsRedirection correct.
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
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS");
        });
    });

    builder.Services.AddEndpoints(Assembly.GetExecutingAssembly());

    builder.Services
        .AddHealthChecks()
        // The database check is not tagged "ready" on purpose. The platform probes /health/ready every
        // few seconds, and a database ping there would keep the Neon compute awake all day. A suspended
        // database is normal operation and not a sign that the app is not ready (ADR-0025). The check
        // is still available on the full /health, and a deploy proves the database through the smoke
        // test's real endpoints.
        .AddNpgSql(
            connectionStringFactory: sp => sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.TranslationDatabase,
            name: "translationdb",
            tags: ["db", "postgres"]);

    const string rateLimitPolicy = "fixed-by-ip";

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy(rateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1)
                }));
    });

    WebApplication app = builder.Build();

    // This has to run before anything that reads the request scheme or host: logging, HSTS, the
    // redirect, authentication and HATEOAS link generation. It is skipped in Development so
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

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseAuthorizationLogging();

    // Create the caller's Translator on their first authenticated request (ADR-0004, amended
    // 2026-06-24), so a user who just registered and logged in already has a TMS profile before any
    // write. It sits after UseAuthorization, so a 401 or 403 stops earlier. The provisioner only writes
    // when the claims changed, so an authenticated read stays a plain lookup.
    app.UseTranslatorProvisioning();

    if (!app.Environment.IsDevelopment() && !app.Environment.IsTesting())
    {
        app.UseRateLimiter();
    }

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }

    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    }).AllowAnonymous();

    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    }).AllowAnonymous();

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready"),
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    }).AllowAnonymous();

    RouteGroupBuilder endpointsGroup = app.MapGroup("");

    if (!app.Environment.IsDevelopment() && !app.Environment.IsTesting())
    {
        endpointsGroup.RequireRateLimiting(rateLimitPolicy);
    }

    app.MapEndpoints(endpointsGroup);

    await app.RunAsync();
}
#pragma warning disable S2139
catch (Exception ex)
{
    Log.Fatal(ex, "TranslationSystem terminated unexpectedly");
    throw;
}
#pragma warning restore S2139
finally
{
    await Log.CloseAndFlushAsync();
}
