using NetArchTest.Rules;

namespace LotroKoniecDev.Architecture.Tests.Unit.Shared;

/// <summary>
/// Turns the types that break a rule into the assertion message. It only formats; the assertion itself
/// stays in the test method, as the repo's testing rules require.
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
