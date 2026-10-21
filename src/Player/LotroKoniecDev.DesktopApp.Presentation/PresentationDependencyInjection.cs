using LotroKoniecDev.Application;
using LotroKoniecDev.DesktopApp.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.DesktopApp.Presentation;

public static class PresentationDependencyInjection
{
    /// <remarks>
    /// Scoped, like the patcher handlers the view models inject from #42 on. The exe resolves them from
    /// one scope that lives as long as the app, as the CLI does.
    /// </remarks>
    public static IServiceCollection AddPresentationServices(this IServiceCollection services)
    {
        services.AddScoped<MainWindowViewModel>();
        services.AddScoped<IProgress<OperationProgress>, MainWindowProgressReporter>();

        return services;
    }
}
