using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// The patcher's Clean Architecture dependency rule: Cli / Infrastructure -> Application -> Domain ->
/// Primitives. Only the forbidden direction is asserted; the allowed one is what the project references
/// already spell out.
/// </summary>
/// <remarks>
/// The desktop app sits beside the Cli as a second shell (ADR-0033): its exe wires both Infrastructure
/// projects, and its UI reaches the patcher through Application only. The Windows Infrastructure is
/// <c>net10.0-windows</c>, so the cross-platform one cannot reference it (NU1201).
/// </remarks>
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
                Namespaces.DesktopApp,
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
                Namespaces.PatcherCli,
                Namespaces.DesktopApp)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Domain sits below Application — it may only reach Primitives:{result.Describe()}");
    }

    [Fact]
    public void ApplicationAssembly_Dependencies_ReachNeitherInfrastructureNorCli()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.PatcherApplication)
            .Should()
            .NotHaveDependencyOnAny(Namespaces.PatcherInfrastructure, Namespaces.PatcherCli, Namespaces.DesktopApp)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Application talks to infrastructure through its own Abstractions/ ports, never the adapters:{result.Describe()}");
    }

    [Fact]
    public void InfrastructureAssembly_Dependencies_ReachNeitherCliNorDesktopApp()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.PatcherInfrastructure)
            .Should()
            .NotHaveDependencyOnAny(Namespaces.PatcherCli, Namespaces.DesktopApp)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"Infrastructure implements Application's ports; the shells wire it, never the other way round:{result.Describe()}");
    }

    [Fact]
    public void DesktopAppPresentationAssembly_Dependencies_ReachNeitherInfrastructureNorCli()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.DesktopAppPresentation)
            .Should()
            .NotHaveDependencyOnAny(Namespaces.PatcherInfrastructure, Namespaces.PatcherCli)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"The desktop UI injects Application's handler interfaces; the exe wires the Windows-only adapters behind them (ADR-0033):{result.Describe()}");
    }
}
