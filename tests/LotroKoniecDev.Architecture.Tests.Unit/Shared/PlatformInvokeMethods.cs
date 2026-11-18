using System.Reflection;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Finds the P/Invoke methods an assembly declares, in every type, compiler-generated ones included,
/// so nothing a generator writes is skipped. A <c>[LibraryImport]</c> that marshals compiles to a
/// generated <c>g____PInvoke</c> method next to the one it wraps.
/// </summary>
internal static class PlatformInvokeMethods
{
    private const BindingFlags EveryDeclaredMethod =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static IReadOnlyList<string> Of(Assembly assembly) =>
        assembly.GetTypes()
            .SelectMany(type => type.GetMethods(EveryDeclaredMethod))
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl))
            .Select(method => $"{method.DeclaringType!.FullName}.{method.Name}")
            .ToList();
}
