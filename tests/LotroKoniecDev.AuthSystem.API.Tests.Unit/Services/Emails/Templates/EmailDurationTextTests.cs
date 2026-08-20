using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails.Templates;

/// <summary>
/// The text reads "Link wygasa po …", so every value has to fit that phrase. The lifetimes come from
/// configuration and must never produce a sentence that sounds wrong.
/// </summary>
public sealed class EmailDurationTextTests
{
    [Theory]
    [InlineData(24, "24 godzinach")]
    [InlineData(1, "1 godzinie")]
    [InlineData(2, "2 godzinach")]
    [InlineData(5, "5 godzinach")]
    [InlineData(36, "36 godzinach")]
    public void Describe_LifespanUnderTwoDays_UsesHours(int hours, string expected)
    {
        // Arrange
        TimeSpan lifespan = TimeSpan.FromHours(hours);

        // Act
        string result = EmailDurationText.Describe(lifespan);

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData(2, "2 dniach")]
    [InlineData(14, "14 dniach")]
    [InlineData(30, "30 dniach")]
    public void Describe_LifespanOfTwoDaysOrMore_UsesDays(int days, string expected)
    {
        // Arrange
        TimeSpan lifespan = TimeSpan.FromDays(days);

        // Act
        string result = EmailDurationText.Describe(lifespan);

        // Assert
        result.ShouldBe(expected);
    }
}
