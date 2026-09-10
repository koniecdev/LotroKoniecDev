using LotroKoniecDev.AuthSystem.API.Middleware;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

/// <summary>
/// The Polish copy a throttled user reads. It is the only user-facing text in the limiter, so the plural
/// forms and the no-number fallback are pinned here rather than left to a reviewer's eye.
/// </summary>
public sealed class TooManyRequestsPageTests
{
    [Theory]
    [InlineData(1, "1 minutę")]
    [InlineData(2, "2 minuty")]
    [InlineData(4, "4 minuty")]
    [InlineData(5, "5 minut")]
    [InlineData(15, "15 minut")]
    public void BuildWaitSentence_ShouldUseThePolishPluralForTheNumber(int minutes, string expected)
    {
        // Act
        string sentence = TooManyRequestsPage.BuildWaitSentence(TimeSpan.FromMinutes(minutes));

        // Assert
        sentence.ShouldContain(expected);
    }

    [Fact]
    public void BuildWaitSentence_ShouldRoundAPartialMinuteUp()
    {
        // Arrange: telling someone to wait 0 minutes is telling them to retry into another refusal
        TimeSpan partialMinute = TimeSpan.FromSeconds(30);

        // Act
        string sentence = TooManyRequestsPage.BuildWaitSentence(partialMinute);

        // Assert
        sentence.ShouldContain("1 minutę");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BuildWaitSentence_ShouldNameNoNumber_WhenTheLimiterGaveNone(int seconds)
    {
        // Act: a policy without retry metadata must not make the page invent a wait
        string sentence = TooManyRequestsPage.BuildWaitSentence(TimeSpan.FromSeconds(seconds));

        // Assert
        sentence.ShouldBe("Odczekaj chwilę i spróbuj ponownie.");
    }
}
