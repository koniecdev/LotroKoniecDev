using System.Globalization;
using Microsoft.AspNetCore.HttpOverrides;
using LotroKoniecDev.Frontend;
using LotroKoniecDev.Frontend.Components;
using LotroKoniecDev.Frontend.Components.Pages.Account;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.CookieConsent;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Infrastructure.Security;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Logging.Redaction;
using LotroKoniecDev.Options;
using LotroKoniecDev.TranslationSystem.Contracts.Import;
using Microsoft.AspNetCore.Http.Features;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

// Not CreateBootstrapLogger(): it lives in the static Log.Logger, and a test run that starts a second
// host from this Program fails with "The logger is already frozen". In production the host's own logger
// still replaces this one.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    Log.Information("Starting Frontend");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // DI validation in every environment (#572): a broken graph fails at startup, where CD smoke-tests
    // the candidate, instead of as a 500 on the first request.
    builder.Host.UseDefaultServiceProvider(static (_, options) =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });

    string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    bool otlpExportEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);

    // Logs reach Loki only through the box agent tailing stdout (ADR-0050 §7), which also catches a crash
    // before logging is up. So the OTLP log sink stays off even when the endpoint is set; only a stack
    // with no tailer turns it on. Alloy drops OTLP log records anyway (ADR-0051).
    bool otlpLogExportEnabled = otlpExportEnabled && string.Equals(
        builder.Configuration["OTEL_LOGS_EXPORTER"],
        "otlp",
        StringComparison.OrdinalIgnoreCase);

    // Two projects share each box and both boxes run as "Production", so these attributes place a
    // signal: service.namespace names the project, deployment.environment names staging or prod.
    // OTEL_DEPLOYMENT_ENVIRONMENT is set per box; when absent, the attribute is omitted, never guessed.
    string? deploymentEnvironment = builder.Configuration["OTEL_DEPLOYMENT_ENVIRONMENT"];
    Dictionary<string, object> telemetryResourceAttributes = new()
    {
        ["service.namespace"] = "lotrokoniecdev"
    };
    if (!string.IsNullOrWhiteSpace(deploymentEnvironment))
    {
        telemetryResourceAttributes["deployment.environment"] = deploymentEnvironment;
    }

    builder.Services.AddSerilog((serviceProvider, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(serviceProvider);

        if (otlpLogExportEnabled)
        {
            loggerConfiguration.WriteTo.OpenTelemetry(opts =>
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

    IOpenTelemetryBuilder openTelemetryBuilder = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource
            // No service.instance.id: a fresh GUID per process becomes a Prometheus label, so every
            // restart would start a new series and break rate() across a deploy.
            .AddService(builder.Environment.ApplicationName, autoGenerateServiceInstanceId: false)
            .AddAttributes(telemetryResourceAttributes))
        .WithTracing(tracing => tracing
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation())
        .WithMetrics(metrics => metrics
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation());

    // Without an endpoint the SDK keeps retrying its default localhost:4317, which no box runs.
    if (otlpExportEnabled)
    {
        openTelemetryBuilder.UseOtlpExporter();
    }

    // TLS ends at the proxy, so Request.Scheme must come from X-Forwarded-Proto for the OIDC
    // redirect_uri, the Secure cookies and UseHttpsRedirection to be right.
    //
    // ForwardedHeaders:KnownNetworks limits trust to the Caddy network (#399); compose.hetzner.yaml and
    // compose.prod.yaml both set it. An empty list would trust every upstream, which is safe only because
    // both stacks use expose: and not ports:. Exactly one proxy sets these headers, hence ForwardLimit = 1.
    // A setting that yields no networks (a scalar missing the __0 index) stops the boot (ADR-0008 §3).
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

    builder.Services.AddHsts(options =>
    {
        options.MaxAge = TimeSpan.FromDays(365);
        options.IncludeSubDomains = true;
        options.Preload = true;
    });

    // exported.txt is about 80 MB and growing, past Kestrel's 30 MB default and the multipart limit, so
    // both take the shared upload size (spec 0003).
    builder.WebHost.ConfigureKestrel(kestrelOptions =>
        kestrelOptions.Limits.MaxRequestBodySize = ImportUploadLimits.MaxUploadBytes);
    builder.Services.Configure<FormOptions>(formOptions =>
        formOptions.MultipartBodyLengthLimit = ImportUploadLimits.MaxUploadBytes);

    builder.Services.AddRazorComponents();

    // No database, so no checks: healthy as soon as the host serves requests.
    builder.Services.AddHealthChecks();

    builder.Services
        .AddOptionsWithFluentValidation<TranslationSystemSettings>(TranslationSystemSettings.ConfigurationSection)
        .AddOptionsWithFluentValidation<AuthSystemSettings>(AuthSystemSettings.ConfigurationSection)
        .AddOptionsWithFluentValidation<DataProtectionSettings>(DataProtectionSettings.ConfigurationSection);

    builder.Services.AddHttpContextAccessor();

    builder.Services.AddFrontendDataProtection(builder.Configuration, builder.Environment);

    builder.Services.AddFrontend();

    WebApplication app = builder.Build();

    // Must run before anything that reads the scheme or host. Outside Development a proxy terminates TLS,
    // and reading the scheme first keeps UseHttpsRedirection from looping.
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    // The query string is logged apart from the path, redacted, so no OAuth code, token or e-mail is
    // stored.
    app.UseSerilogRequestLogging(options =>
    {
        options.IncludeQueryInRequestPath = false;
        options.MessageTemplate =
            "HTTP {RequestMethod} {RequestPath}{RequestQuery} responded {StatusCode} in {Elapsed:0.0000} ms";
        options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            diagnosticContext.Set("RequestQuery", SensitiveDataRedactor.RedactQueryString(httpContext.Request.QueryString.Value));
    });

    if (!app.Environment.IsDevelopment())
    {
        app.UseSecurityHeaders();
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePagesByStatusCode(
        statusCode => statusCode switch
        {
            StatusCodes.Status400BadRequest => "/bad-request",
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => "/auth/access-denied",
            StatusCodes.Status404NotFound => "/not-found",
            StatusCodes.Status503ServiceUnavailable => "/auth/login-unavailable",
            _ => "/Error"
        },
        createScopeForStatusCodePages: true);

    app.UseHttpsRedirection();

    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseAntiforgery();

    app.MapStaticAssets();

    app.MapHealthChecks("/health/live").AllowAnonymous();
    app.MapHealthChecks("/health/ready").AllowAnonymous();

    app.MapAuthEndpoints();
    app.MapImportExportEndpoints();
    app.MapAccountEndpoints();
    app.MapCookieConsentEndpoints();
    app.MapRazorComponents<App>();

    await app.RunAsync();
}
#pragma warning disable S2139
catch (Exception ex)
{
    Log.Fatal(ex, "Frontend terminated unexpectedly");
    throw;
}
#pragma warning restore S2139
finally
{
    await Log.CloseAndFlushAsync();
}

/// <summary>
/// Public so the integration test host (<c>WebApplicationFactory</c>) can reference the Frontend's
/// entry-point assembly.
/// </summary>
public partial class Program;
