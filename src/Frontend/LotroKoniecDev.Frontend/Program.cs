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
    Log.Information("Starting Frontend");

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

    // Add the OTLP exporter only when an endpoint is configured. Otherwise the SDK keeps retrying
    // against the default localhost:4317 collector, which does not exist in the cloud.
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        openTelemetryBuilder.UseOtlpExporter();
    }

    // Behind the reverse proxy, TLS ends at the proxy and the container gets plain HTTP with
    // X-Forwarded-* headers. We read them so Request.Scheme is https, which keeps the OIDC redirect_uri,
    // the antiforgery and Secure cookies and UseHttpsRedirection correct.
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

    // HSTS settings (audit #0001, M7): a max-age of one year, covering subdomains and marked for the
    // preload list. UseHsts() only sends it outside Development, where the deployed stack is HTTPS only
    // behind the ingress. The local dev loop never sends it, so localhost over HTTP still works.
    builder.Services.AddHsts(options =>
    {
        options.MaxAge = TimeSpan.FromDays(365);
        options.IncludeSubDomains = true;
        options.Preload = true;
    });

    // The admin-only import form uploads exported.txt, which is about 80 MB and keeps growing, far past
    // Kestrel's 30 MB default body limit and the framework's multipart form limit. Both are raised to
    // the shared upload size, so the whole file reaches this host in one request and is then sent on to
    // the TMS API (spec 0003, #208).
    builder.WebHost.ConfigureKestrel(kestrelOptions =>
        kestrelOptions.Limits.MaxRequestBodySize = ImportUploadLimits.MaxUploadBytes);
    builder.Services.Configure<FormOptions>(formOptions =>
        formOptions.MultipartBodyLengthLimit = ImportUploadLimits.MaxUploadBytes);

    builder.Services.AddRazorComponents();

    // The liveness and readiness endpoints the ingress probes. The Frontend has no database, so an empty
    // set of checks is enough: they report healthy as soon as the host is serving requests.
    builder.Services.AddHealthChecks();

    builder.Services
        .AddOptionsWithFluentValidation<TranslationSystemSettings>(TranslationSystemSettings.ConfigurationSection)
        .AddOptionsWithFluentValidation<AuthSystemSettings>(AuthSystemSettings.ConfigurationSection)
        .AddOptionsWithFluentValidation<DataProtectionSettings>(DataProtectionSettings.ConfigurationSection);

    builder.Services.AddHttpContextAccessor();

    // Set up before the host is built, straight from configuration. The keyring has to be kept and the
    // application name has to stay the same, so the cookies, the antiforgery tokens and the OIDC
    // correlation survive a restart and work across replicas.
    builder.Services.AddFrontendDataProtection(builder.Configuration, builder.Environment);

    builder.Services.AddFrontend();

    WebApplication app = builder.Build();

    // This has to run before anything that reads the request scheme or host: logging, HSTS, the redirect
    // and the OIDC correlation. It is skipped in Development so the local dev loop keeps working as it
    // is, and it is on in Testing and Production, where a proxy terminates TLS and sets
    // X-Forwarded-Proto. Because the scheme is read first, UseHttpsRedirection below does nothing and
    // there is no redirect loop.
    if (!app.Environment.IsDevelopment())
    {
        app.UseForwardedHeaders();
    }

    // Clean the request log (audit #0001, M5): keep the query string out of RequestPath and log it
    // separately with secrets removed and e-mails masked, so no OAuth code, token or personal data is
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
