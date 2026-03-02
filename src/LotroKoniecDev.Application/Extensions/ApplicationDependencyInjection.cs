using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Behaviors;
using LotroKoniecDev.Application.Features.UpdateChecking;
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
        services.AddMediator(options =>
        {
            options.ServiceLifetime = ServiceLifetime.Scoped;
            options.PipelineBehaviors =
            [
                typeof(RequestLoggingPipelineBehavior<,>),
                typeof(ValidationPipelineBehavior<,>)
            ];
        });
        
        services.AddSingleton<ITranslationParser, TranslationFileParser>();
        services.AddSingleton<IGameUpdateChecker, GameUpdateChecker>();

        return services;
    }
}
