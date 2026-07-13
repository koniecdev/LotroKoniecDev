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

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting Frontend");

    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

    builder.Services.AddSerilog((serviceProvider, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(serviceProvider);

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
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
                opts.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = builder.Environment.ApplicationName
                };
            });
        }
    });

    IOpenTelemetryBuilder openTelemetryBuilder = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
        .WithTracing(tracing => tracing
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation())
        .WithMetrics(metrics => metrics
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation());

    // Wire the OTLP exporter only when an endpoint is configured; otherwise the SDK keeps retrying
    // against the default localhost:4317 collector, which does not exist in the cloud runtime.
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        openTelemetryBuilder.UseOtlpExporter();
    }

    // Behind the reverse proxy TLS terminates at the proxy and the container receives plain HTTP
    // with X-Forwarded-* headers. Honour them so Request.Scheme is https, which keeps the OIDC
    // redirect_uri, antiforgery/Secure cookies, and UseHttpsRedirection correct.
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

    // HSTS hardening (audit #0001 / M7): a one-year max-age covering subdomains and flagged for the
    // preload list. Only emitted by UseHsts() outside Development (the deployed stack is HTTPS-only
    // behind the ingress); the dev host loop never sends it, so localhost HTTP is unaffected.
    builder.Services.AddHsts(options =>
    {
        options.MaxAge = TimeSpan.FromDays(365);
        options.IncludeSubDomains = true;
        options.Preload = true;
    });

    // The Blazor SSR import form (admin-only) uploads exported.txt, which is ~80 MB and grows — far
    // past Kestrel's 30 MB default body cap and the framework's multipart form-length limit. Lift both
    // to the shared upload ceiling so the whole file posts to this host in one request, then streams on
    // to the TMS API (spec 0003, #208).
    builder.WebHost.ConfigureKestrel(kestrelOptions =>
        kestrelOptions.Limits.MaxRequestBodySize = ImportUploadLimits.MaxUploadBytes);
    builder.Services.Configure<FormOptions>(formOptions =>
        formOptions.MultipartBodyLengthLimit = ImportUploadLimits.MaxUploadBytes);

    builder.Services.AddRazorComponents();

    // Liveness/readiness endpoints for the cloud ingress probes (ACA). The Frontend has no backing
    // store, so an empty check set is enough — they report healthy once the host is serving requests.
    builder.Services.AddHealthChecks();

    builder.Services
        .AddOptionsWithFluentValidation<TranslationSystemSettings>(TranslationSystemSettings.ConfigurationSection)
        .AddOptionsWithFluentValidation<AuthSystemSettings>(AuthSystemSettings.ConfigurationSection)
        .AddOptionsWithFluentValidation<DataProtectionSettings>(DataProtectionSettings.ConfigurationSection);

    builder.Services.AddHttpContextAccessor();

    // Configured before the host is built and read straight from configuration: the keyring must be
    // persistent + the application name pinned so cookies/antiforgery/OIDC correlation survive restarts
    // and are shared across replicas.
    builder.Services.AddFrontendDataProtection(builder.Configuration, builder.Environment);

    builder.Services.AddFrontend();

    WebApplication app = builder.Build();

    // Must run before anything that reads the request scheme/host (logging, HSTS, redirect, OIDC
    // correlation). Skipped in Development so the host-run dev workflow keeps its plain behaviour
    // unchanged; active in Testing + Production where a TLS-terminating proxy sets X-Forwarded-Proto.
    // With the proto honoured first, UseHttpsRedirection below is a no-op (the scheme already reads
    // https) — there is no redirect loop.
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    // Redact the request log (audit #0001 / M5): keep RequestPath query-free and log the query
    // separately with secrets stripped and e-mails masked, so no OAuth code/token or PII is persisted.
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
/// Exposed so the integration test host (<c>WebApplicationFactory</c>) can reference the Frontend's
/// entry-point assembly.
/// </summary>
public partial class Program;
