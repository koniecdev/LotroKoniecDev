using System.Reflection;
using LotroKoniecDev.Architecture.Tests.Unit.Shared;
using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// Proves the suite can still say "no". Every other test here asserts an ABSENCE, so a broken search set
/// or a silently empty type scan would report a green that means nothing.
/// </summary>
public sealed class SuiteSelfTests
{
    [Fact]
    public void DependencyRule_AgainstADependencyThatGenuinelyExists_Fails()
    {
        TestResult result = Types.InAssembly(ProductionAssemblies.Frontend)
            .Should()
            .NotHaveDependencyOn(Namespaces.TranslationSystemContracts)
            .GetResult();

        result.IsSuccessful.ShouldBeFalse(
            "the Frontend does reference TranslationSystem.Contracts — a rule that misses it detects nothing at all");
    }

    [Fact]
    public void TypeScan_EveryProductionAssembly_ContributesAtLeastOneType()
    {
        List<string> emptyAssemblies = ProductionAssemblies.All
            .Where(assembly => ProductionTypes.Of(assembly).Count == 0)
            .Select(assembly => assembly.GetName().Name!)
            .ToList();

        emptyAssemblies.ShouldBeEmpty(
            $"An assembly nothing was read from is an assembly no convention rule covers:{ViolationReport.Format(emptyAssemblies)}");
    }

    /// <summary>
    /// Catches the half-done wiring: a new production project gets its <c>ProjectReference</c> here but
    /// never joins <see cref="ProductionAssemblies.All"/>, so no rule ever sees it.
    /// </summary>
    /// <remarks>
    /// It reads this suite's own build output, the same directory <c>Assembly.Load</c> already uses, so it
    /// gives the same answer every time, in any order, on any platform.
    /// <c>GetReferencedAssemblies()</c> cannot do this: it only lists the assemblies the compiler really
    /// bound, and most of the search set is loaded by name and never referenced at compile time.
    /// </remarks>
    [Fact]
    public void SearchSet_EveryProductionAssemblyInTheBuildOutput_IsUnderRule()
    {
        HashSet<string> underRule = ProductionAssemblies.All
            .Select(assembly => assembly.GetName().Name!)
            .ToHashSet(StringComparer.Ordinal);

        List<string> unwatched = Directory
            .GetFiles(AppContext.BaseDirectory, "LotroKoniecDev.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !name.EndsWith(".Tests.Unit", StringComparison.Ordinal))
            .Where(name => !underRule.Contains(name))
            .ToList();

        unwatched.ShouldBeEmpty(
            $"A new production project must join ProductionAssemblies.All, or it silently escapes every rule:{ViolationReport.Format(unwatched)}");
    }
}
