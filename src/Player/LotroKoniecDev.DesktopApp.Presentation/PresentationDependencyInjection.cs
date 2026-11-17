using LotroKoniecDev.Application;
using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.DesktopApp.Presentation;

public static class PresentationDependencyInjection
{
    /// <remarks>
    /// Scoped, like the patcher handlers the view models inject from #42 on. The exe resolves them from
    /// one scope that lives as long as the app, as the CLI does. The exe also registers the
    /// <see cref="ISettingsFile"/> and <see cref="IFileProbe"/> adapters.
    /// </remarks>
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        services.AddScoped<ProgressBarViewModel>();

        // The handlers get the very progress bar the main window shows.
        services.AddScoped<IProgress<OperationProgress>>(
            serviceProvider => serviceProvider.GetRequiredService<ProgressBarViewModel>());

        services.AddSingleton<IPathPicker, AvaloniaPathPicker>();
        services.AddScoped<DesktopSettingsStore>();
        services.AddScoped<GameFolderViewModel>();
        services.AddScoped<SettingsViewModel>();
        services.AddScoped<MainWindowViewModel>();

        return services;
    }
}
