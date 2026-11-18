using System.Runtime.InteropServices;
using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// The patcher's <c>net10.0</c> assemblies run on any OS, so they call no native library (ADR-0033,
/// 2026-11-18 amendment). CA1416 stops a Windows-only .NET API and NU1201 a reference to the Windows
/// project; these rules stop the two ways around both. Native code belongs in
/// <c>LotroKoniecDev.Infrastructure.Windows</c>.
/// </summary>
public sealed class PatcherPlatformTests
{
    [Fact]
    public void PatcherAssemblies_Methods_DeclareNoPlatformInvoke()
    {
        List<string> platformInvokes = ProductionAssemblies.Patcher
            .SelectMany(PlatformInvokeMethods.Of)
            .ToList();

        platformInvokes.ShouldBeEmpty(
            $"A P/Invoke compiles in a net10.0 project but fails at run time where its library is missing:{ViolationReport.Format(platformInvokes)}");
    }

    [Fact]
    public void PatcherAssemblies_Dependencies_NeverLoadANativeLibraryByHand()
    {
        TestResult result = Types.InAssemblies(ProductionAssemblies.Patcher)
            .Should()
            .NotHaveDependencyOn(typeof(NativeLibrary).FullName!)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"NativeLibrary loads native code without a P/Invoke, so the rule above cannot see it:{result.Describe()}");
    }
}
