using System.Reflection;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Handles on every cross-platform production assembly, loaded by name out of the test output.
/// </summary>
/// <remarks>
/// The two patcher <c>net10.0-windows</c> assemblies (Infrastructure, Cli) are missing on purpose — a
/// <c>net10.0</c> project cannot reference them, and this suite must stay green on the Linux CI runner.
/// They sit at the TOP of the patcher layering, so every rule about them is stated as a forbidden
/// NAMESPACE on the assemblies below (see <see cref="Namespaces.PatcherInfrastructure"/>), which needs
/// no reference at all.
/// </remarks>
internal static class ProductionAssemblies
{
    internal static Assembly PatcherPrimitives { get; } = Load(Namespaces.PatcherPrimitives);

    internal static Assembly PatcherDomain { get; } = Load(Namespaces.PatcherDomain);

    internal static Assembly PatcherApplication { get; } = Load(Namespaces.PatcherApplication);

    internal static Assembly SharedKernel { get; } = Load(Namespaces.SharedKernel);

    internal static Assembly TranslationSystemPrimitives { get; } = Load(Namespaces.TranslationSystemPrimitives);

    internal static Assembly TranslationSystemDomain { get; } = Load(Namespaces.TranslationSystemDomain);

    internal static Assembly TranslationSystemReadModels { get; } = Load(Namespaces.TranslationSystemReadModels);

    internal static Assembly TranslationSystemReadModelsEntityFramework { get; } =
        Load(Namespaces.TranslationSystemReadModelsEntityFramework);

    internal static Assembly TranslationSystemProjections { get; } = Load(Namespaces.TranslationSystemProjections);

    internal static Assembly TranslationSystemPersistence { get; } = Load(Namespaces.TranslationSystemPersistence);

    internal static Assembly TranslationSystemContracts { get; } = Load(Namespaces.TranslationSystemContracts);

    internal static Assembly TranslationSystemApi { get; } = Load(Namespaces.TranslationSystemApi);

    internal static Assembly AuthSystemDomain { get; } = Load(Namespaces.AuthSystemDomain);

    internal static Assembly AuthSystemContracts { get; } = Load(Namespaces.AuthSystemContracts);

    internal static Assembly AuthSystemInfrastructure { get; } = Load(Namespaces.AuthSystemInfrastructure);

    internal static Assembly AuthSystemPersistence { get; } = Load(Namespaces.AuthSystemPersistence);

    internal static Assembly AuthSystemApi { get; } = Load(Namespaces.AuthSystemApi);

    internal static Assembly Frontend { get; } = Load(Namespaces.Frontend);

    internal static Assembly HateoasAbstractions { get; } = Load(Namespaces.HateoasAbstractions);

    internal static Assembly Hateoas { get; } = Load(Namespaces.Hateoas);

    internal static Assembly Logging { get; } = Load(Namespaces.Logging);

    internal static Assembly Options { get; } = Load(Namespaces.Options);

    /// <summary>Every assembly above — the search set for repo-wide rules.</summary>
    internal static IReadOnlyList<Assembly> All { get; } =
    [
        PatcherPrimitives,
        PatcherDomain,
        PatcherApplication,
        SharedKernel,
        TranslationSystemPrimitives,
        TranslationSystemDomain,
        TranslationSystemReadModels,
        TranslationSystemReadModelsEntityFramework,
        TranslationSystemProjections,
        TranslationSystemPersistence,
        TranslationSystemContracts,
        TranslationSystemApi,
        AuthSystemDomain,
        AuthSystemContracts,
        AuthSystemInfrastructure,
        AuthSystemPersistence,
        AuthSystemApi,
        Frontend,
        HateoasAbstractions,
        Hateoas,
        Logging,
        Options,
    ];

    private static Assembly Load(string assemblyName) => Assembly.Load(new AssemblyName(assemblyName));
}
