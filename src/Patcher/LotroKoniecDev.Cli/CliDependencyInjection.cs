using LotroKoniecDev.Application;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Cli.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Cli;

public static class CliDependencyInjection
{
    public static IServiceCollection AddCliServices(this IServiceCollection services)
    {
        services.AddSingleton<IDatPathResolver, DatPathResolver>();
        services.AddSingleton<IBackupManager, BackupManager>();
        services.AddSingleton<IFileProvider, FileProvider>();
        services.AddSingleton<IOperationStatusReporter, ConsoleStatusReporter>();
        services.AddSingleton<IProgress<OperationProgress>, ConsoleProgressReporter>();

        return services;
    }
}
