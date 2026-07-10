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

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting TranslationSystem");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

    // Behind a cloud ingress (ACA / ALB / any reverse proxy) TLS terminates at the proxy and the
    // container receives plain HTTP with X-Forwarded-* headers. Honour them so Request.Scheme is
    // https, which keeps the JWT issuer, HATEOAS hrefs, Secure cookies, and UseHttpsRedirection
    // correct.
    //
    // Trust policy (#399): where the proxy subnet is knowable, ForwardedHeaders:KnownNetworks
    // restricts trust to those CIDRs (compose.prod.yaml pins the Caddy network and sets it); where
    // the ingress hop has no stable IP (ACA), the list stays empty — every upstream is trusted —
    // which is safe only under the recorded invariant that the container port is NEVER published
    // directly: ACA ingress is the sole route (iac/) and compose.prod.yaml uses expose:, not ports:.
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
        // The db check is deliberately NOT tagged "ready": ACA probes /health/ready every few
        // seconds, and a DB ping there keeps the scale-to-zero Neon compute awake 24/7 (a suspended
        // database is normal operation, not unreadiness — ADR-0025). The check stays reachable on
        // demand via the full /health; deploys prove the DB through the smoke's real endpoints.
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

    // Must run before anything that reads the request scheme/host (logging, HSTS, redirect, auth,
    // HATEOAS link generation). Skipped in Development so `docker compose up` keeps its plain-http
    // behaviour unchanged; active in Testing + Production/Staging where a TLS-terminating proxy sets
    // X-Forwarded-Proto. With the proto honoured first, UseHttpsRedirection below is a no-op (the
    // scheme already reads https) — there is no redirect loop.
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

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseAuthorizationLogging();

    // Eagerly provision the caller's Translator on their first authenticated request (ADR-0004
    // amendment 2026-06-24), so a freshly registered + logged-in user already has a TMS profile
    // before any write. Placed after UseAuthorization so a 401/403 short-circuits ahead of it; the
    // provisioner only writes when claims changed, so authenticated reads stay a pure lookup.
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
