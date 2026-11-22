using LotroKoniecDev.AuthSystem.API.Pages.Account;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// The Grafana rule <c>lotro-unconfirmed-sign-in</c> counts auth-api's log lines by their text (#1510).
/// A renamed prefix or property would leave the rule with no data, which Grafana reports as OK, so the
/// alert would stop with no sign of it. The project copies the rule file into <c>TestData</c>, and it is
/// a code input in <c>scripts/ci/classify-changes.sh</c>, so an edit to it runs this test.
/// </summary>
public sealed class UnconfirmedSignInLogRuleTests
{
    private const string RuleUidLine = "uid: lotro-unconfirmed-sign-in";
    private const string GroupNameKey = "name: ";

    private static readonly string RuleFilePath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "lotrokoniecdev-alerts.yaml");

    [Fact]
    public async Task RuleFile_ShouldReadAuthApiLinesAndFilterOnThePrefixAnchoredToTheTemplateKey()
    {
        // Act
        string rules = await ReadRuleFileAsync();

        // Assert: a bare-text filter would also match a caller's header or path in a request line.
        RuleBlockOf(rules).ShouldContain(
            $"container=~\".*auth-api.*\"}} |= `\"@mt\":\"{LoginLogText.EmailNotConfirmedPrefix}`");
        rules.ShouldNotContain($"|= `{LoginLogText.EmailNotConfirmedPrefix}");
    }

    [Fact]
    public async Task RuleFile_ShouldGroupTheRuleByEnvironmentAndTheLoggedAccountId()
    {
        // Act
        string rule = RuleBlockOf(await ReadRuleFileAsync());

        // Assert
        rule.ShouldContain("| json UserId=\"UserId\"");
        rule.ShouldContain("sum by (environment, UserId)");
    }

    /// <summary>
    /// The owner's decision on #1510: every attempt fires, with no threshold and no wait.
    /// </summary>
    [Fact]
    public async Task RuleFile_ShouldFireOnTheFirstLineWithNoWait()
    {
        // Act
        string rule = RuleBlockOf(await ReadRuleFileAsync());

        // Assert
        rule.ShouldContain("for: 0s");
        rule.ShouldContain("evaluator: { type: gt, params: [0] }");
    }

    /// <summary>
    /// The runbook promises the first mail in about 90 seconds, which holds only in the one-minute group.
    /// </summary>
    [Fact]
    public async Task RuleFile_ShouldPutTheRuleInTheOneMinuteGroup()
    {
        // Act
        string groupHeader = GroupHeaderOf(await ReadRuleFileAsync());

        // Assert
        groupHeader.Split('\n')[0].TrimEnd('\r').ShouldBe($"{GroupNameKey}lotrokoniecdev");
        groupHeader.ShouldContain("interval: 1m");
    }

    [Fact]
    public void EmailNotConfirmedTemplate_ShouldNameThePropertyTheRuleExtracts()
    {
        LoginLogText.EmailNotConfirmedTemplate.ShouldContain("{UserId}");
    }

    private static Task<string> ReadRuleFileAsync() => File.ReadAllTextAsync(RuleFilePath);

    /// <summary>
    /// The rule's own lines, from its uid to the next rule or group.
    /// </summary>
    private static string RuleBlockOf(string rules)
    {
        int ruleAt = rules.IndexOf(RuleUidLine, StringComparison.Ordinal);
        if (ruleAt < 0)
        {
            return string.Empty;
        }

        int[] nextBlockStarts =
        [
            rules.IndexOf("- uid:", ruleAt, StringComparison.Ordinal),
            rules.IndexOf("- orgId:", ruleAt, StringComparison.Ordinal),
            rules.Length
        ];
        return rules[ruleAt..nextBlockStarts.Where(index => index >= 0).Min()];
    }

    /// <summary>
    /// Only a group has a name key, so the last one above the rule starts the rule's group. The header
    /// ends where the group's rules start.
    /// </summary>
    private static string GroupHeaderOf(string rules)
    {
        int ruleAt = rules.IndexOf(RuleUidLine, StringComparison.Ordinal);
        if (ruleAt < 0)
        {
            return string.Empty;
        }

        int groupAt = rules.LastIndexOf(GroupNameKey, ruleAt, StringComparison.Ordinal);
        if (groupAt < 0)
        {
            return string.Empty;
        }

        int rulesKeyAt = rules.IndexOf("rules:", groupAt, StringComparison.Ordinal);
        return rules[groupAt..(rulesKeyAt < 0 ? ruleAt : rulesKeyAt)];
    }
}
