using System.Reflection;
using System.Runtime.CompilerServices;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Hand-written types of the production assemblies — everything the compiler synthesised (closures,
/// async state machines, <c>[GeneratedRegex]</c> helpers, the top-level-statement entry point) is
/// dropped, because no house rule can bind code nobody wrote.
/// </summary>
/// <remarks>
/// Reflection, not NetArchTest, backs the CONVENTION rules: NetArchTest's predicate DSL cannot express
/// "sealed unless another production type inherits it", nor read a validator's generic argument. The
/// DEPENDENCY rules stay on NetArchTest, which scans the full IL of every member.
/// </remarks>
internal static class ProductionTypes
{
    internal static IReadOnlyList<Type> All { get; } =
        ProductionAssemblies.All.SelectMany(Of).ToList();

    internal static IReadOnlyList<Type> Of(Assembly assembly) =>
        assembly.GetTypes().Where(type => !IsCompilerGenerated(type)).ToList();

    /// <summary>
    /// Every production type closing at least one of <paramref name="openGenericInterfaces"/> — the way
    /// to find "all query handlers" or "all validators" without guessing at type names.
    /// </summary>
    internal static IReadOnlyList<Type> ImplementingAny(params Type[] openGenericInterfaces) =>
        All.Where(type => ClosedInterfacesOf(type, openGenericInterfaces).Count > 0).ToList();

    internal static IReadOnlyList<Type> ClosedInterfacesOf(Type type, params Type[] openGenericInterfaces) =>
        type.GetInterfaces()
            .Where(candidate => candidate.IsGenericType
                && openGenericInterfaces.Contains(candidate.GetGenericTypeDefinition()))
            .ToList();

    internal static bool Implements(Type type, Type openGenericInterface) =>
        ClosedInterfacesOf(type, openGenericInterface).Count > 0;

    private static bool IsCompilerGenerated(Type type)
    {
        for (Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || current.Name.Contains('<'))
            {
                return true;
            }
        }

        return false;
    }
}
