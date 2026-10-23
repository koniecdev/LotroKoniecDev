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
    [InlineData(11, "11 minut")]
    [InlineData(12, "12 minut")]
    [InlineData(13, "13 minut")]
    [InlineData(14, "14 minut")]
    [InlineData(15, "15 minut")]
    [InlineData(21, "21 minut")]
    [InlineData(22, "22 minuty")]
    [InlineData(23, "23 minuty")]
    [InlineData(24, "24 minuty")]
    [InlineData(25, "25 minut")]
    [InlineData(101, "101 minut")]
    [InlineData(112, "112 minut")]
    [InlineData(122, "122 minuty")]
    public void BuildWaitSentence_ShouldUseThePolishPluralForTheNumber(int minutes, string expected)
    {
        // Act
        string sentence = TooManyRequestsPage.BuildWaitSentence(TimeSpan.FromMinutes(minutes));

        // Assert
        sentence.ShouldBe("Spróbuj ponownie później. Limit odnowi się najpóźniej za " + expected + ".");
    }

    [Fact]
    public void BuildWaitSentence_ShouldRoundAPartialMinuteUp()
    {
        // Arrange: telling someone to wait 0 minutes is telling them to retry into another refusal
        TimeSpan partialMinute = TimeSpan.FromSeconds(30);

        // Act
        string sentence = TooManyRequestsPage.BuildWaitSentence(partialMinute);

        // Assert
        sentence.ShouldBe("Spróbuj ponownie później. Limit odnowi się najpóźniej za 1 minutę.");
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

    [Fact]
    public void BuildHtml_ShouldTellTheUserTheLongestWait()
    {
        // Act: the wait is the one thing a throttled user can act on (#692). The limiter's number is an
        // upper bound, so the page names it as the longest wait, never as an estimate (#892).
        string html = TooManyRequestsPage.BuildHtml(TimeSpan.FromMinutes(3));

        // Assert
        html.ShouldContain("Spróbuj ponownie później. Limit odnowi się najpóźniej za 3 minuty.");
    }

    [Fact]
    public void BuildHtml_ShouldCarryNoInlineStyleOrScript()
    {
        // Act: the limiter writes this page outside Razor, under the same CSP as every page (#693, #833)
        string html = TooManyRequestsPage.BuildHtml(TimeSpan.FromMinutes(3));

        // Assert
        html.ShouldContain("<link rel=\"stylesheet\" href=\"/error-page.css\" />");
        html.ShouldNotContain("<style");
        html.ShouldNotContain("<script");
    }
}
