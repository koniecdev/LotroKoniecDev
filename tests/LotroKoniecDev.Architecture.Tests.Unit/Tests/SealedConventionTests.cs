using LotroKoniecDev.Architecture.Tests.Unit.Shared;

namespace LotroKoniecDev.Architecture.Tests.Unit.Tests;

/// <summary>
/// The house rule "seal every type unless there is explicit inheritance", stated mechanically: a class is
/// sealed unless another production type actually derives from it.
/// </summary>
/// <remarks>
/// Types whose shape a framework decides are out of scope: a generated EF migration, a Razor component,
/// a view, an <c>_Imports</c> marker and the <c>Program</c> marker from top-level statements cannot
/// carry the modifier without editing generated code by hand. Everything else has to be sealed.
/// </remarks>
public sealed class SealedConventionTests
{
    private static readonly string[] FrameworkShapedBaseTypes =
    [
        "Microsoft.EntityFrameworkCore.Migrations.Migration",
        "Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot",
        "Microsoft.AspNetCore.Components.ComponentBase",
        "Microsoft.AspNetCore.Mvc.Razor.RazorPageBase",
        "Microsoft.AspNetCore.Mvc.RazorPages.PageModel",
    ];

    /// <summary>
    /// Real violations that existed before this suite, each fixed under its own ticket. #570 only added
    /// tests. The key is the full type name and the value is the ticket that removes the entry. It has
    /// been empty since #599.
    /// </summary>
    private static readonly Dictionary<string, string> KnownViolations = new(StringComparer.Ordinal);

    [Fact]
    public void ProductionClasses_WithoutAnExplicitSubclass_AreSealed()
    {
        List<string> violations = UnsealedClassesNothingDerivesFrom()
            .Where(typeName => !KnownViolations.ContainsKey(typeName))
            .ToList();

        violations.ShouldBeEmpty(
            $"Seal it, or give it a subclass — the repo has no third option:{ViolationReport.Format(violations)}");
    }

    [Fact]
    public void KnownViolations_EveryEntry_StillBreaksTheRule()
    {
        IReadOnlyList<string> currentViolations = UnsealedClassesNothingDerivesFrom();

        List<string> staleEntries = KnownViolations
            .Where(violation => !currentViolations.Contains(violation.Key))
            .Select(violation => $"{violation.Key} (was to be fixed under {violation.Value})")
            .ToList();

        staleEntries.ShouldBeEmpty(
            $"The quarantine is self-cleaning — a fixed type has to leave KnownViolations, or the rule stops covering it:{ViolationReport.Format(staleEntries)}");
    }

    private static IReadOnlyList<string> UnsealedClassesNothingDerivesFrom()
    {
        HashSet<Type> baseTypes = ProductionTypes.All
            .Select(type => type.BaseType)
            .OfType<Type>()
            .Select(Normalize)
            .ToHashSet();

        return ProductionTypes.All
            .Where(type => type.IsClass && !type.IsAbstract && !type.IsSealed)
            .Where(type => !baseTypes.Contains(Normalize(type)))
            .Where(type => !IsFrameworkShaped(type))
            .Select(type => type.FullName!)
            .ToList();
    }

    private static bool IsFrameworkShaped(Type type) =>
        IsEntryPointMarker(type) || IsRazorImportsMarker(type) || InheritsFrameworkShape(type);

    private static bool IsEntryPointMarker(Type type) =>
        type.Namespace is null && type.Name is "Program";

    /// <summary>Razor emits one empty marker class per <c>_Imports.razor</c>; it derives from nothing.</summary>
    private static bool IsRazorImportsMarker(Type type) => type.Name is "_Imports";

    private static bool InheritsFrameworkShape(Type type)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (FrameworkShapedBaseTypes.Contains(Normalize(current).FullName))
            {
                return true;
            }
        }

        return false;
    }

    private static Type Normalize(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;
}
