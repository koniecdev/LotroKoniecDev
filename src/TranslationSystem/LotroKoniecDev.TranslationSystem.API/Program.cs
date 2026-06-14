using System.Globalization;
using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
using LotroKoniecDev.TranslationSystem.API.Features.Bootstrap;
using LotroKoniecDev.TranslationSystem.API.Health;
using LotroKoniecDev.TranslationSystem.API.Middleware;
using LotroKoniecDev.TranslationSystem.Persistence.Settings;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting TranslationSystem");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

    builder.Services.AddTranslationSystem();
    builder.Services.AddJwtBearerAuthentication(builder.Environment);

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

    builder.Services.Configure<RouteHandlerOptions>(options =>
    {
        options.ThrowOnBadRequest = true;
    });

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
                .WithOrigins("https://lotro.koniec.dev")
                .WithHeaders("Authorization", "Content-Type", "X-Requested-With", "Accept")
                .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS");
        });
    });

    builder.Services.AddEndpoints(Assembly.GetExecutingAssembly());

    builder.Services
        .AddHealthChecks()
        .AddNpgSql(
            connectionStringFactory: sp => sp.GetRequiredService<IOptions<ConnectionStringSettings>>().Value.TranslationDatabase,
            name: "translationdb",
            tags: ["db", "postgres", "ready"]);

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

    string corsPolicy = app.Environment.EnvironmentName switch
    {
        EnvironmentsExtensions.ProductionName => productionCorsPolicy,
        _ => localCorsPolicy
    };
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

    // One-time, opt-in DB bootstrap (spec 0001, first run / #28): baseline import + polish.txt seed.
    // Disabled by default; runs after the schema is in place and before the app serves traffic.
    await app.BootstrapTranslationsAsync();

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
