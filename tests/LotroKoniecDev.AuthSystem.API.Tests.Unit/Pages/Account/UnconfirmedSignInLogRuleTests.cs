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
    private const string GroupNameKey = "name: ";

    private static readonly string RuleFilePath =
        Path.Combine(AppContext.BaseDirectory, "TestData", "lotrokoniecdev-alerts.yaml");

    [Fact]
    public async Task RuleFile_ShouldReadAuthApiLinesAndFilterOnThePrefixAnchoredToTheTemplateKey()
    {
        // Act
        string rules = await ReadRuleFileAsync();

        // Assert: a bare-text filter would also match a caller's header or path in a request line.
        rules.ShouldContain(
            $"container=~\".*auth-api.*\"}} |= `\"@mt\":\"{LoginLogText.EmailNotConfirmedPrefix}`");
        rules.ShouldNotContain($"|= `{LoginLogText.EmailNotConfirmedPrefix}");
    }

    [Fact]
    public async Task RuleFile_ShouldGroupTheRuleByEnvironmentAndTheLoggedAccountId()
    {
        // Act
        string rules = await ReadRuleFileAsync();

        // Assert
        rules.ShouldContain("| json UserId=\"UserId\"");
        rules.ShouldContain("sum by (environment, UserId)");
    }

    /// <summary>
    /// The daily group runs every five minutes, so the first mail would come minutes later.
    /// </summary>
    [Fact]
    public async Task RuleFile_ShouldPutTheRuleInTheOneMinuteGroup()
    {
        // Act
        string rules = await ReadRuleFileAsync();

        // Assert: only a group has a name key, so the last one above the rule is its group.
        int ruleAt = rules.IndexOf("uid: lotro-unconfirmed-sign-in", StringComparison.Ordinal);
        ruleAt.ShouldBeGreaterThan(0);
        string aboveTheRule = rules[..ruleAt];
        int groupNameAt = aboveTheRule.LastIndexOf(GroupNameKey, StringComparison.Ordinal) + GroupNameKey.Length;
        aboveTheRule[groupNameAt..].Split('\n')[0].TrimEnd('\r').ShouldBe("lotrokoniecdev");
    }

    [Fact]
    public void EmailNotConfirmedTemplate_ShouldNameThePropertyTheRuleExtracts()
    {
        LoginLogText.EmailNotConfirmedTemplate.ShouldContain("{UserId}");
    }

    private static Task<string> ReadRuleFileAsync() => File.ReadAllTextAsync(RuleFilePath);
}
