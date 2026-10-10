using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails.Templates;

/// <summary>
/// The texts read "Link wygasa po …" and "Link działa przez …", so every value has to fit its phrase. The
/// lifetimes come from configuration and must never produce a sentence that sounds wrong.
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

    [Theory]
    [InlineData(1, "1 godzinę")]
    [InlineData(2, "2 godziny")]
    [InlineData(4, "4 godziny")]
    [InlineData(5, "5 godzin")]
    [InlineData(12, "12 godzin")]
    [InlineData(21, "21 godzin")]
    [InlineData(22, "22 godziny")]
    [InlineData(24, "24 godziny")]
    [InlineData(36, "36 godzin")]
    public void DescribeFor_LifespanUnderTwoDays_UsesHoursInTheFormThatFollowsPrzez(int hours, string expected)
    {
        // Arrange
        TimeSpan lifespan = TimeSpan.FromHours(hours);

        // Act
        string result = EmailDurationText.DescribeFor(lifespan);

        // Assert
        result.ShouldBe(expected);
    }

    [Theory]
    [InlineData(2, "2 dni")]
    [InlineData(14, "14 dni")]
    [InlineData(22, "22 dni")]
    [InlineData(30, "30 dni")]
    public void DescribeFor_LifespanOfTwoDaysOrMore_UsesDaysInTheFormThatFollowsPrzez(int days, string expected)
    {
        // Arrange
        TimeSpan lifespan = TimeSpan.FromDays(days);

        // Act
        string result = EmailDurationText.DescribeFor(lifespan);

        // Assert
        result.ShouldBe(expected);
    }
}
