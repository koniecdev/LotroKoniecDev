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
using LotroKoniecDev.TranslationSystem.API.Services.RateLimiting;
using LotroKoniecDev.TranslationSystem.API.Settings;
using LotroKoniecDev.TranslationSystem.Persistence.Settings;
using LotroKoniecDev.Logging.Redaction;

// Not CreateBootstrapLogger(): it lives in the static Log.Logger, and a test run that starts a second
// host from this Program fails with "The logger is already frozen". In production the host's own logger
// still replaces this one.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    Log.Information("Starting TranslationSystem");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // In every environment (#572), so a bad graph fails at startup, where CD smoke-tests the candidate,
    // not as a 500. It checks registered services only: endpoint integration tests still guard a
    // forgotten closed handler registration.
    builder.Host.UseDefaultServiceProvider(static (_, options) =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });

    string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    bool otlpExportEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);

    // Logs reach Loki only through the box agent tailing stdout (ADR-0050 §7), which also catches a
    // crash before the logging pipeline is up. So the OTLP log sink stays off even when the endpoint is
    // set; only a stack with no tailer turns it on. Alloy drops OTLP log records anyway (ADR-0051).
    bool otlpLogExportEnabled = otlpExportEnabled && string.Equals(
        builder.Configuration["OTEL_LOGS_EXPORTER"],
        "otlp",
        StringComparison.OrdinalIgnoreCase);

    // Two projects share each box and both boxes run "Production", so these attributes place a signal:
    // service.namespace tells the projects apart, deployment.environment staging from prod. Without
    // OTEL_DEPLOYMENT_ENVIRONMENT the attribute is omitted, never guessed.
    string? deploymentEnvironment = builder.Configuration["OTEL_DEPLOYMENT_ENVIRONMENT"];
    Dictionary<string, object> telemetryResourceAttributes = new()
    {
        ["service.namespace"] = "lotrokoniecdev"
    };
    if (!string.IsNullOrWhiteSpace(deploymentEnvironment))
    {
        telemetryResourceAttributes["deployment.environment"] = deploymentEnvironment;
    }

    builder.Services.AddSerilog((services, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration)
          .ReadFrom.Services(services);

        if (otlpLogExportEnabled)
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
                opts.ResourceAttributes = new Dictionary<string, object>(telemetryResourceAttributes)
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
        .ConfigureResource(resource => resource
            // No service.instance.id: a fresh GUID per process becomes a Prometheus label, so every
            // restart would start a new series and break rate() across a deploy.
            .AddService(builder.Environment.ApplicationName, autoGenerateServiceInstanceId: false)
            .AddAttributes(telemetryResourceAttributes))
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

    // Without an endpoint the SDK keeps retrying its default localhost:4317, which no box runs.
    if (otlpExportEnabled)
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

    // TLS ends at the proxy, so reading X-Forwarded-* keeps the scheme https for the JWT issuer, the
    // HATEOAS hrefs and the redirect. ForwardedHeaders:KnownNetworks must match the Caddy network in
    // compose.hetzner.yaml and compose.prod.yaml (#399). An empty list trusts every upstream, which is
    // safe only because both stacks use expose:, not ports:. One proxy sets the headers, so
    // ForwardLimit = 1. A setting that yields no networks (a scalar missing the __0 index) stops the boot
    // instead of trusting more (ADR-0008 §3).
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

    // Origins come from the environment and are checked at startup, so a missing or malformed origin
    // stops the boot instead of quietly blocking the browser (ADR-0008 §3). Development ignores them.
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
        // Not tagged "ready": a ping on every readiness probe would keep the Neon compute awake all day,
        // and a suspended database is normal operation (ADR-0025). The full /health still runs it.
        .AddNpgSql(
            connectionStringFactory: sp => sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.TranslationDatabase,
            name: "translationdb",
            tags: ["db", "postgres"]);

    // The full /health runs only for a caller with this key (ADR-0058).
    builder.Services.AddOptions<HealthCheckSettings>()
        .BindConfiguration(HealthCheckSettings.ConfigurationSection)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<HealthCheckSettings>, HealthCheckSettingsValidator>();

    // Every frontend call comes from one container, so the rate-limit key is the visitor's address the
    // frontend forwards next to the environment's key, or else the caller's own (ADR-0054).
    builder.Services.AddOptions<FrontendCallerSettings>()
        .BindConfiguration(FrontendCallerSettings.ConfigurationSection)
        .ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<FrontendCallerSettings>, FrontendCallerSettingsValidator>();
    builder.Services.AddSingleton<RateLimitPartitionKeyResolver>();

    const string rateLimitPolicy = "fixed-by-ip";

    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddPolicy(rateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.RequestServices.GetRequiredService<RateLimitPartitionKeyResolver>().Resolve(httpContext),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 100,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // A fixed window reports its whole length, not the time left, so Retry-After is an upper bound
        // (#855, #892). Rounding up keeps it one.
        options.OnRejected = (context, _) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(NumberFormatInfo.InvariantInfo);
            }

            return ValueTask.CompletedTask;
        };
    });

    WebApplication app = builder.Build();

    // Before anything that reads the scheme or host. Reading the forwarded scheme first is also what
    // keeps UseHttpsRedirection from looping. Development runs over plain http with no proxy.
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    app.UseRequestContextLogging();

    app.UseSerilogRequestLogging(options =>
    {
        // The query string is logged separately, redacted, so no OAuth code, token or e-mail is stored
        // (audit #0001).
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

    string corsPolicy = app.Environment.IsDevelopment()
        ? localCorsPolicy
        : productionCorsPolicy;
    app.UseCors(corsPolicy);

    // Before authentication (#829): a limiter after it would never count the 401s, and a call over the
    // limit stops before translator provisioning can write. After routing, so it can read the policy.
    // RateLimiting:ForceEnable lets a test host see a real 429.
    bool rateLimiterOffByEnvironment = app.Environment.IsDevelopment() || app.Environment.IsTesting();
    if (!rateLimiterOffByEnvironment || app.Configuration.GetValue<bool>("RateLimiting:ForceEnable"))
    {
        app.UseRateLimiter();
    }

    // Around authentication and authorization, so it sees the 401s and 403s they answer themselves
    // (#854).
    app.UseAuthorizationLogging();

    app.UseAuthentication();
    app.UseAuthorization();

    // Creates the caller's Translator on the first authenticated request (ADR-0004, amended
    // 2026-06-24). After UseAuthorization, so a 401 or 403 stops earlier.
    app.UseTranslatorProvisioning();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }

    // The full /health queries the database, so it needs the key (ADR-0058). The probes below run no
    // checks and stay open.
    app.MapKeyGatedHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = HealthCheckResponseWriter.WriteResponse
    });

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

    // Always attached; UseRateLimiter above decides whether it is enforced.
    RouteGroupBuilder endpointsGroup = app.MapGroup("")
        .RequireRateLimiting(rateLimitPolicy);

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
