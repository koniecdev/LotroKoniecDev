using System.Globalization;
using LotroKoniecDev.Frontend;
using LotroKoniecDev.Frontend.Components;
using LotroKoniecDev.Frontend.Components.Pages.ImportExport;
using LotroKoniecDev.Frontend.Infrastructure.Auth;
using LotroKoniecDev.Frontend.Infrastructure.Errors;
using LotroKoniecDev.Frontend.Settings;
using LotroKoniecDev.Options;
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

    builder.Services.AddSerilog((serviceProvider, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(builder.Configuration)
            .ReadFrom.Services(serviceProvider);

        string? otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
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

    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
        .WithTracing(tracing => tracing
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation())
        .WithMetrics(metrics => metrics
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation())
        .UseOtlpExporter();

    builder.Services.AddRazorComponents();

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

    app.UseSerilogRequestLogging();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseStatusCodePagesByStatusCode(
        statusCode => statusCode switch
        {
            StatusCodes.Status400BadRequest => "/bad-request",
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden => "/auth/access-denied",
            StatusCodes.Status404NotFound => "/not-found",
            _ => "/Error"
        },
        createScopeForStatusCodePages: true);

    app.UseHttpsRedirection();

    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();

    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapAuthEndpoints();
    app.MapImportExportEndpoints();
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
