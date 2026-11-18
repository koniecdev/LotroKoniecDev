using System.Reflection;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Every cross-platform production assembly, loaded by name out of the test output.
/// </summary>
/// <remarks>
/// The patcher's Windows Infrastructure and Cli, and the desktop app's exe, target
/// <c>net10.0-windows</c>, which a <c>net10.0</c> project cannot reference. They sit at the top of the
/// patcher's layers, so every rule about them is written as a forbidden namespace on the assemblies
/// below (see <see cref="Namespaces.PatcherInfrastructure"/>, a prefix that covers both Infrastructure
/// projects).
/// </remarks>
internal static class ProductionAssemblies
{
    /// <summary>The other side of the repo: the TMS, the sign-in server, the website and the shared helpers.</summary>
    private static readonly string[] OutsideThePatcher =
    [
        Namespaces.SharedKernel,
        Namespaces.TranslationSystem,
        Namespaces.AuthSystem,
        Namespaces.Frontend,
        Namespaces.Hateoas,
        Namespaces.Logging,
        Namespaces.Options,
    ];

    internal static Assembly PatcherPrimitives { get; } = Load(Namespaces.PatcherPrimitives);

    internal static Assembly PatcherDomain { get; } = Load(Namespaces.PatcherDomain);

    internal static Assembly PatcherApplication { get; } = Load(Namespaces.PatcherApplication);

    internal static Assembly PatcherInfrastructure { get; } = Load(Namespaces.PatcherInfrastructure);

    internal static Assembly DesktopAppPresentation { get; } = Load(Namespaces.DesktopAppPresentation);

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

    /// <summary>
    /// What the repo-wide rules search. A new production project must join it, or it escapes every rule;
    /// <c>SuiteSelfTests</c> fails until it does.
    /// </summary>
    internal static IReadOnlyList<Assembly> All { get; } =
    [
        PatcherPrimitives,
        PatcherDomain,
        PatcherApplication,
        PatcherInfrastructure,
        DesktopAppPresentation,
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

    /// <summary>
    /// The patcher side's <c>net10.0</c> assemblies: everything in <see cref="All"/> outside the other
    /// side of the repo. The desktop player app is the patcher's second shell, so its UI sits here too.
    /// It is derived, so a new patcher project joins it by joining <see cref="All"/>.
    /// </summary>
    internal static IReadOnlyList<Assembly> Patcher { get; } =
        All.Where(assembly => !IsOutsideThePatcher(assembly)).ToList();

    private static Assembly Load(string assemblyName) => Assembly.Load(new AssemblyName(assemblyName));

    private static bool IsOutsideThePatcher(Assembly assembly) =>
        OutsideThePatcher.Any(prefix => assembly.GetName().Name!.StartsWith(prefix, StringComparison.Ordinal));
}
