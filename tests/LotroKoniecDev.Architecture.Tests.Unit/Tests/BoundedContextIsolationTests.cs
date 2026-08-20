using System.Reflection;
using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// The patcher and the TMS are two bounded contexts in one repository. They share a data contract, the
/// <c>||</c> translation file, and never code. The TMS runs in Linux containers and must never touch the
/// x86 Windows native interop, and the patcher runs on a gaming machine and must never touch the
/// database.
/// </summary>
public sealed class BoundedContextIsolationTests
{
    private static readonly Assembly[] TranslationManagementAssemblies =
    [
        ProductionAssemblies.TranslationSystemPrimitives,
        ProductionAssemblies.TranslationSystemDomain,
        ProductionAssemblies.TranslationSystemReadModels,
        ProductionAssemblies.TranslationSystemReadModelsEntityFramework,
        ProductionAssemblies.TranslationSystemProjections,
        ProductionAssemblies.TranslationSystemPersistence,
        ProductionAssemblies.TranslationSystemContracts,
        ProductionAssemblies.TranslationSystemApi,
        ProductionAssemblies.AuthSystemDomain,
        ProductionAssemblies.AuthSystemContracts,
        ProductionAssemblies.AuthSystemInfrastructure,
        ProductionAssemblies.AuthSystemPersistence,
        ProductionAssemblies.AuthSystemApi,
        ProductionAssemblies.Frontend,
    ];

    private static readonly Assembly[] PatcherAssemblies =
    [
        ProductionAssemblies.PatcherPrimitives,
        ProductionAssemblies.PatcherDomain,
        ProductionAssemblies.PatcherApplication,
    ];

    [Fact]
    public void TranslationManagementAssemblies_Dependencies_NeverReachThePatcher()
    {
        TestResult result = Types.InAssemblies(TranslationManagementAssemblies)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.PatcherPrimitives,
                Namespaces.PatcherDomain,
                Namespaces.PatcherApplication,
                Namespaces.PatcherInfrastructure,
                Namespaces.PatcherCli)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The TMS owns its own parser/serializer — it never links the patcher, least of all its native interop:{result.Describe()}");
    }

    [Fact]
    public void PatcherAssemblies_Dependencies_NeverReachTheTranslationManagementSystem()
    {
        TestResult result = Types.InAssemblies(PatcherAssemblies)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.TranslationSystem,
                Namespaces.AuthSystem,
                Namespaces.Frontend,
                Namespaces.SharedKernel)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The patcher duplicates the few building blocks it needs on purpose — it never links the TMS side:{result.Describe()}");
    }

    [Fact]
    public void FrontendAssembly_Dependencies_ReachTheContractsAndNothingBehindThem()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.Frontend)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.TranslationSystemDomain,
                Namespaces.TranslationSystemReadModels,
                Namespaces.TranslationSystemProjections,
                Namespaces.TranslationSystemPersistence,
                Namespaces.TranslationSystemApi,
                Namespaces.AuthSystemDomain,
                Namespaces.AuthSystemInfrastructure,
                Namespaces.AuthSystemPersistence,
                Namespaces.AuthSystemApi)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The Frontend is an HTTP client — it sees Contracts (+ Primitives), never a domain or a DbContext:{result.Describe()}");
    }

    [Fact]
    public void TranslationSystemAssemblies_Dependencies_NeverReachTheAuthServerInternals()
    {
        Assembly[] translationSystemAssemblies = TranslationManagementAssemblies
            .Where(assembly => assembly.GetName().Name?.StartsWith(Namespaces.TranslationSystem, StringComparison.Ordinal) == true)
            .ToArray();

        TestResult result = Types.InAssemblies(translationSystemAssemblies)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.AuthSystemDomain,
                Namespaces.AuthSystemInfrastructure,
                Namespaces.AuthSystemPersistence,
                Namespaces.AuthSystemApi)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The TMS trusts the auth server over JWT/JWKS only — it never links its Identity model or its database:{result.Describe()}");
    }
}
