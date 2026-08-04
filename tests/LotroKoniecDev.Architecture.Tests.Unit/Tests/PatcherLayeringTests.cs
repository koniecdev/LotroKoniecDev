using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// The patcher's Clean Architecture dependency rule: Cli / Infrastructure -> Application -> Domain ->
/// Primitives. Only the forbidden direction is asserted; the allowed one is what the project references
/// already spell out.
/// </summary>
public sealed class PatcherLayeringTests
{
    [Fact]
    public void PrimitivesAssembly_Dependencies_ReachNoOtherProjectInTheRepository()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.PatcherPrimitives)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.PatcherDomain,
                Namespaces.PatcherApplication,
                Namespaces.PatcherInfrastructure,
                Namespaces.PatcherCli,
                Namespaces.SharedKernel,
                Namespaces.TranslationSystem,
                Namespaces.AuthSystem,
                Namespaces.Frontend,
                Namespaces.Hateoas,
                Namespaces.Logging,
                Namespaces.Options)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Primitives is the bottom layer — constants and enums, zero dependencies:{result.Describe()}");
    }

    [Fact]
    public void DomainAssembly_Dependencies_ReachNeitherApplicationInfrastructureNorCli()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.PatcherDomain)
            .Should()
            .NotHaveDependencyOnAny(
                Namespaces.PatcherApplication,
                Namespaces.PatcherInfrastructure,
                Namespaces.PatcherCli)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain sits below Application — it may only reach Primitives:{result.Describe()}");
    }

    [Fact]
    public void ApplicationAssembly_Dependencies_ReachNeitherInfrastructureNorCli()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.PatcherApplication)
            .Should()
            .NotHaveDependencyOnAny(Namespaces.PatcherInfrastructure, Namespaces.PatcherCli)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application talks to infrastructure through its own Abstractions/ ports, never the adapters:{result.Describe()}");
    }
}
