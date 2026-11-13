using LotroKoniecDev.TranslationSystem.API.Features.Translations;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

/// <summary>
/// The Grafana rules count tms-api's log lines by their text (ADR-0072). A renamed prefix or property
/// would leave a rule with no data, which Grafana reports as OK, so the alert would stop with no sign
/// of it. The rule file is a code input in <c>scripts/ci/classify-changes.sh</c>, so an edit to it
/// runs this test.
/// </summary>
public sealed class TranslationSaveBudgetLogRuleTests
{
    private static readonly string RuleFilePath = Path.Combine(
        ".docker", "observability", "grafana", "provisioning", "alerting", "lotrokoniecdev-alerts.yaml");

    [Theory]
    [InlineData(TranslationSaveBudgetLogText.BudgetPrefix)]
    [InlineData(TranslationSaveBudgetLogText.OverwrittenPrefix)]
    public async Task RuleFile_ShouldFilterOnThePrefixAnchoredToTheTemplateKey(string prefix)
    {
        // Act
        string rules = await ReadRuleFileAsync();

        // Assert: a bare-text filter would also match a caller's header or path in a request line.
        rules.ShouldContain($"|= `\"@mt\":\"{prefix}`");
        rules.ShouldNotContain($"|= `{prefix}");
    }

    [Fact]
    public async Task RuleFile_ShouldGroupTheBudgetRuleByTheLoggedProperties()
    {
        // Act
        string rules = await ReadRuleFileAsync();

        // Assert
        rules.ShouldContain("| json TranslatorId=\"TranslatorId\", Budget=\"Budget\", Outcome=\"Outcome\"");
        rules.ShouldContain("sum by (environment, TranslatorId, Budget, Outcome)");
    }

    [Theory]
    [InlineData("{TranslatorId}")]
    [InlineData("{Budget}")]
    [InlineData("{Outcome}")]
    public void BudgetTemplate_ShouldNameThePropertyTheRuleExtracts(string placeholder)
    {
        TranslationSaveBudgetLogText.BudgetTemplate.ShouldContain(placeholder);
    }

    private static Task<string> ReadRuleFileAsync()
        => File.ReadAllTextAsync(Path.Combine(FindSolutionDirectory(), RuleFilePath));

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LotroKoniecDev.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the solution directory (LotroKoniecDev.slnx) above {AppContext.BaseDirectory}.");
    }
}
