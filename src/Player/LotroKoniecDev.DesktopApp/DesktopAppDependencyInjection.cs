using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LotroKoniecDev.DesktopApp;

/// <summary>
/// The composition root: the same patcher handlers and adapters the CLI uses, plus the desktop UI in
/// place of the console (ADR-0033 §3).
/// </summary>
internal static class DesktopAppDependencyInjection
{
    /// <remarks>
    /// The handlers' log lines go to Serilog's static logger, which <see cref="Program"/> points at the
    /// log file, as the CLI does.
    /// </remarks>
    public static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddApplicationServices();
        services.AddInfrastructureServices();
        services.AddPresentationServices();

        // DI validation runs in every environment (#572). A handler that cannot be built stops the app
        // before any window opens, and Program writes the reason to the log file.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}
