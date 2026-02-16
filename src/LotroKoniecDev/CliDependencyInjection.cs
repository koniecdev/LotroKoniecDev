using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev;

public static class CliDependencyInjection
{
    public static IServiceCollection AddCliServices(this IServiceCollection services)
    {
        services.AddSingleton<IDatPathResolver, DatPathResolver>();
        services.AddSingleton<IBackupManager, BackupManager>();
        services.AddSingleton<IPreflightChecker, PreflightChecker>();
        services.AddSingleton<IFileProvider, FileProvider>();
        services.AddSingleton<IOperationStatusReporter, ConsoleStatusReporter>();

        return services;
    }
}
