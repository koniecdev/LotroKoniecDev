using LotroKoniecDev.AuthSystem.API.Common;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Common;

/// <summary>
/// Every auth text that puts a noun after a count goes through this rule, so the numbers a short
/// "2 to 4" rule gets wrong are pinned here once (#1032).
/// </summary>
public sealed class PolishPluralTests
{
    [Theory]
    [InlineData(0, "many")]
    [InlineData(1, "one")]
    [InlineData(2, "few")]
    [InlineData(3, "few")]
    [InlineData(4, "few")]
    [InlineData(5, "many")]
    [InlineData(11, "many")]
    [InlineData(12, "many")]
    [InlineData(13, "many")]
    [InlineData(14, "many")]
    [InlineData(15, "many")]
    [InlineData(21, "many")]
    [InlineData(22, "few")]
    [InlineData(23, "few")]
    [InlineData(24, "few")]
    [InlineData(25, "many")]
    [InlineData(101, "many")]
    [InlineData(102, "few")]
    [InlineData(111, "many")]
    [InlineData(112, "many")]
    [InlineData(113, "many")]
    [InlineData(114, "many")]
    [InlineData(122, "few")]
    [InlineData(1004, "few")]
    public void Pick_ShouldChooseTheFormThatFollowsTheCount(int count, string expected)
    {
        // Act
        string form = PolishPlural.Pick(count, "one", "few", "many");

        // Assert
        form.ShouldBe(expected);
    }
}
