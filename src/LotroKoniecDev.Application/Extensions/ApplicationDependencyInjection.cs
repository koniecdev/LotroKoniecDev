using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Features.Export;
using LotroKoniecDev.Application.Features.Patch;
using LotroKoniecDev.Application.Parsers;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Application.Extensions;

/// <summary>
/// Extension methods for registering application layer services.
/// </summary>
public static class ApplicationDependencyInjection
{
    /// <summary>
    /// Adds application layer services to the service collection.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<ITranslationParser, TranslationFileParser>();
        services.AddScoped<IExporter, Exporter>();
        services.AddScoped<IPatcher, Patcher>();

        return services;
    }
}
