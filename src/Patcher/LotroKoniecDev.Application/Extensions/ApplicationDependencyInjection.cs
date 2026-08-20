using FluentValidation;
using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.Messaging;
using LotroKoniecDev.Application.Features.Exporting;
using LotroKoniecDev.Application.Features.GameLaunching;
using LotroKoniecDev.Application.Features.Patching;
using LotroKoniecDev.Application.Features.PreflightChecking;
using LotroKoniecDev.Application.Features.TranslationFileSyncing;
using LotroKoniecDev.Application.Features.UpdateChecking;
using LotroKoniecDev.Application.Parsers;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.Application.Extensions;

public static class ApplicationDependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IQueryHandler<ExportTextsQuery, Result<ExportSummaryResponse>>, ExportTextsQueryHandler>();
        services.AddScoped<IQueryHandler<PreflightCheckQuery, Result<PreflightReportResponse>>, PreflightCheckQueryHandler>();
        services.AddScoped<ICommandHandler<ApplyPatchCommand, Result<PatchSummaryResponse>>, ApplyPatchCommandHandler>();
        services.AddScoped<ICommandHandler<GameLaunchingCommand, Result<GameLaunchingResponse>>, GameLaunchingCommandHandler>();
        services.AddScoped<ICommandHandler<SyncTranslationFileCommand, Result<TranslationFileSyncResponse>>, SyncTranslationFileCommandHandler>();

        services.AddSingleton<IValidator<ApplyPatchCommand>, ApplyPatchCommandValidator>();
        services.AddSingleton<IValidator<GameLaunchingCommand>, GameLaunchingCommandValidator>();
        services.AddSingleton<IValidator<SyncTranslationFileCommand>, SyncTranslationFileCommandValidator>();

        services.AddSingleton<ITranslationParser, TranslationFileParser>();
        services.AddSingleton<IGameUpdateChecker, GameUpdateChecker>();
        services.AddScoped<IPatchingService, PatchingService>();
        services.AddScoped<ITranslationFileEndpointResolver, TranslationFileEndpointResolver>();
        services.AddScoped<IVersionBaselineService, VersionBaselineService>();
        services.AddScoped<IGameLaunchingStrategy, SimplifiedGameLaunchingStrategy>();

        return services;
    }
}
