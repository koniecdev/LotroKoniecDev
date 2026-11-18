using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Infrastructure.Windows.AppUpdating;
using LotroKoniecDev.Infrastructure.Windows.DatFile;
using LotroKoniecDev.Infrastructure.Windows.GameLaunching;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Infrastructure.Windows;

/// <summary>
/// Registers the five ports only Windows can serve. A host on Windows also calls
/// <c>AddInfrastructureServices()</c>; a Linux host puts stand-ins in their place (ADR-0033).
/// </summary>
public static class WindowsInfrastructureDependencyInjection
{
    public static IServiceCollection AddWindowsInfrastructureServices(this IServiceCollection services)
    {
        services.AddScoped<DatFileHandler>();
        services.AddScoped<IDatFileHandler>(sp => sp.GetRequiredService<DatFileHandler>());
        services.AddScoped<IDatVersionReader>(sp => sp.GetRequiredService<DatFileHandler>());
        services.AddSingleton<IGameLauncher, GameLauncher>();
        services.AddSingleton<IDatFileLocator, DatFileLocator>();
        services.AddSingleton<IInstallerFileOpener, InstallerFileOpener>();

        return services;
    }
}
