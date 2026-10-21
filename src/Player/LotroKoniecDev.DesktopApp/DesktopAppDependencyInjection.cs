using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.DesktopApp;

/// <summary>
/// The composition root: the same patcher handlers and adapters the CLI uses, plus the desktop UI in
/// place of the console (ADR-0033 §3).
/// </summary>
internal static class DesktopAppDependencyInjection
{
    public static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddApplicationServices();
        services.AddInfrastructureServices();
        services.AddPresentationServices();

        // DI validation runs in every environment (#572). A handler that cannot be built fails the app
        // at startup, before any window opens, not halfway through a DAT write.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }
}
