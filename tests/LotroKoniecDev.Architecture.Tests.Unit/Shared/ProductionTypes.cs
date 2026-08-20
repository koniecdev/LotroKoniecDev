using System.Reflection;
using System.Runtime.CompilerServices;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// The types someone wrote by hand in the production assemblies. Everything the compiler generated is
/// left out: closures, async state machines, <c>[GeneratedRegex]</c> helpers and the entry point built
/// from top-level statements. No house rule can apply to code nobody wrote.
/// </summary>
/// <remarks>
/// The convention rules use reflection and not NetArchTest, because NetArchTest cannot express "sealed
/// unless another production type inherits it", and it cannot read a validator's generic argument. The
/// dependency rules stay on NetArchTest, which scans the full IL of every member.
/// </remarks>
internal static class ProductionTypes
{
    internal static IReadOnlyList<Type> All { get; } =
        ProductionAssemblies.All.SelectMany(Of).ToList();

    internal static IReadOnlyList<Type> Of(Assembly assembly) =>
        assembly.GetTypes().Where(type => !IsCompilerGenerated(type)).ToList();

    /// <summary>
    /// Every production type that implements at least one of <paramref name="openGenericInterfaces"/>.
    /// This is how we find "all query handlers" or "all validators" without guessing at type names.
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
