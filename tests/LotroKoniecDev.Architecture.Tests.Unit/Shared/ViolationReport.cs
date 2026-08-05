using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Formats a rule's offenders into the assertion message. Pure formatting — the assertion itself stays
/// inline in the test method, per the repo's testing conventions.
/// </summary>
internal static class ViolationReport
{
    internal static string Describe(this TestResult result) =>
        result.IsSuccessful
            ? "no violating types"
            : Format(result.FailingTypeNames);

    internal static string Format(IEnumerable<string> violatingTypeNames) =>
        string.Concat(violatingTypeNames.Order().Select(name => $"{Environment.NewLine}  - {name}"));
}
