using LotroKoniecDev.TranslationSystem.API.Features.TranslationFiles;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.TranslationFiles;

public sealed class TranslationFileRebuildSchedulerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Schedule_WithBlankLanguage_ShouldThrow(string? language)
    {
        // Arrange
        TranslationFileRebuildScheduler scheduler = new();

        // Act & Assert: a blank language is a programmer error, not a business failure.
        Should.Throw<ArgumentException>(() => scheduler.Schedule(language!));
        scheduler.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void Schedule_ShouldEnqueueSignalAndTrackItAsPending()
    {
        // Arrange
        TranslationFileRebuildScheduler scheduler = new();

        // Act
        scheduler.Schedule("pl");

        // Assert
        scheduler.PendingCount.ShouldBe(1);
        scheduler.Reader.TryRead(out string? language).ShouldBeTrue();
        language.ShouldBe("pl");
    }

    [Fact]
    public void MarkCompleted_AfterAllSignalsRebuilt_ShouldReturnToIdle()
    {
        // Arrange: a burst of signals the worker later drains as one batch.
        TranslationFileRebuildScheduler scheduler = new();
        scheduler.Schedule("pl");
        scheduler.Schedule("pl");
        scheduler.Schedule("pl");

        // Act
        scheduler.MarkCompleted(3);

        // Assert: idle means every scheduled rebuild has finished, which is what the tests wait for.
        scheduler.PendingCount.ShouldBe(0);
    }
}
