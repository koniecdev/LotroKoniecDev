namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Root namespace of every production assembly, plus the third-party roots the rules forbid.
/// </summary>
/// <remarks>
/// NetArchTest matches a dependency when its full type name STARTS WITH the given string, so these
/// constants are prefixes, not exact names: <see cref="Hateoas"/> also covers
/// <see cref="HateoasAbstractions"/>, and <see cref="TranslationSystemReadModels"/> also covers
/// <see cref="TranslationSystemReadModelsEntityFramework"/>. Pick the narrowest prefix that states
/// the boundary a rule defends.
/// </remarks>
internal static class Namespaces
{
    internal const string PatcherPrimitives = "LotroKoniecDev.Primitives";
    internal const string PatcherDomain = "LotroKoniecDev.Domain";
    internal const string PatcherApplication = "LotroKoniecDev.Application";
    internal const string PatcherInfrastructure = "LotroKoniecDev.Infrastructure";
    internal const string PatcherCli = "LotroKoniecDev.Cli";

    internal const string SharedKernel = "LotroKoniecDev.SharedKernel";

    internal const string TranslationSystem = "LotroKoniecDev.TranslationSystem";
    internal const string TranslationSystemPrimitives = "LotroKoniecDev.TranslationSystem.Primitives";
    internal const string TranslationSystemDomain = "LotroKoniecDev.TranslationSystem.Domain";
    internal const string TranslationSystemReadModels = "LotroKoniecDev.TranslationSystem.ReadModels";
    internal const string TranslationSystemReadModelsEntityFramework = "LotroKoniecDev.TranslationSystem.ReadModels.EntityFramework";
    internal const string TranslationSystemProjections = "LotroKoniecDev.TranslationSystem.Projections";
    internal const string TranslationSystemPersistence = "LotroKoniecDev.TranslationSystem.Persistence";
    internal const string TranslationSystemWriteDbContexts = "LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts";
    internal const string TranslationSystemContracts = "LotroKoniecDev.TranslationSystem.Contracts";
    internal const string TranslationSystemApi = "LotroKoniecDev.TranslationSystem.API";

    internal const string AuthSystem = "LotroKoniecDev.AuthSystem";
    internal const string AuthSystemDomain = "LotroKoniecDev.AuthSystem.Domain";
    internal const string AuthSystemContracts = "LotroKoniecDev.AuthSystem.Contracts";
    internal const string AuthSystemInfrastructure = "LotroKoniecDev.AuthSystem.Infrastructure";
    internal const string AuthSystemPersistence = "LotroKoniecDev.AuthSystem.Persistence";
    internal const string AuthSystemApi = "LotroKoniecDev.AuthSystem.API";

    internal const string Frontend = "LotroKoniecDev.Frontend";

    internal const string Hateoas = "LotroKoniecDev.Hateoas";
    internal const string HateoasAbstractions = "LotroKoniecDev.Hateoas.Abstractions";
    internal const string Logging = "LotroKoniecDev.Logging";
    internal const string Options = "LotroKoniecDev.Options";

    internal const string EntityFrameworkCore = "Microsoft.EntityFrameworkCore";
    internal const string Npgsql = "Npgsql";

    /// <summary>The two mediator packages ADR-0001 forbids repo-wide.</summary>
    internal const string MediatR = "MediatR";

    /// <inheritdoc cref="MediatR"/>
    internal const string Mediator = "Mediator";
}
