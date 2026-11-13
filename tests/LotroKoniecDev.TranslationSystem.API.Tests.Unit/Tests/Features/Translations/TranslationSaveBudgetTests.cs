using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Persistence.SaveBudget;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

public sealed class TranslationSaveBudgetTests
{
    private static readonly TranslationSaveBudgetSettings Defaults = new();

    [Theory]
    [InlineData(1_000, false)]
    [InlineData(1_001, true)]
    public void Judge_AtAndOneOverTheSaveLimit_ShouldRefuseOnlyOver(int saves, bool expectRefused)
    {
        // Act
        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(new TranslationSaveCounts(saves, 0), false, Defaults);

        // Assert
        (verdict.RefusedBy is TranslationSaveBudgetKind.Saves).ShouldBe(expectRefused);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void Judge_AtAndOneOverTheOverwriteLimit_ShouldRefuseOnlyOver(int overwrites, bool expectRefused)
    {
        // Act
        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(new TranslationSaveCounts(overwrites, overwrites), true, Defaults);

        // Assert
        (verdict.RefusedBy is TranslationSaveBudgetKind.Overwrites).ShouldBe(expectRefused);
    }

    [Fact]
    public void Judge_OverTheOverwriteLimitOnAnUntranslatedRow_ShouldAllow()
    {
        // Act
        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(new TranslationSaveCounts(300, 200), false, Defaults);

        // Assert
        verdict.RefusedBy.ShouldBeNull();
        verdict.OverwritesAtAlertThreshold.ShouldBeFalse();
    }

    [Fact]
    public void Judge_WhenBothBudgetsAreSpent_ShouldNameTheSaveBudget()
    {
        // Act
        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(new TranslationSaveCounts(1_001, 101), true, Defaults);

        // Assert
        verdict.RefusedBy.ShouldBe(TranslationSaveBudgetKind.Saves);
    }

    [Theory]
    [InlineData(499, false)]
    [InlineData(500, true)]
    [InlineData(999, true)]
    public void Judge_AroundTheSaveAlertThreshold_ShouldFlagFromTheThreshold(int saves, bool expectAlert)
    {
        // Act
        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(new TranslationSaveCounts(saves, 0), false, Defaults);

        // Assert
        verdict.SavesAtAlertThreshold.ShouldBe(expectAlert);
    }

    [Theory]
    [InlineData(49, true, false)]
    [InlineData(50, true, true)]
    [InlineData(50, false, false)]
    public void Judge_AroundTheOverwriteAlertThreshold_ShouldFlagOnlyAnOverwrite(int overwrites, bool replacesPolish, bool expectAlert)
    {
        // Act
        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(new TranslationSaveCounts(overwrites, overwrites), replacesPolish, Defaults);

        // Assert
        verdict.OverwritesAtAlertThreshold.ShouldBe(expectAlert);
    }

    /// <summary>
    /// The busiest prod account, measured read-only on 2026-11-13 (#687): 86 saves in an hour, 216 in
    /// a day, about 300 with the saves an approval hid. None of it may reach a refusal or an alert.
    /// </summary>
    [Theory]
    [InlineData(86)]
    [InlineData(216)]
    [InlineData(300)]
    public void Judge_TheMeasuredBusiestDayUnderTheDefaults_ShouldNeitherRefuseNorAlert(int saves)
    {
        // Act
        TranslationSaveBudgetVerdict verdict = TranslationSaveBudget.Judge(new TranslationSaveCounts(saves, 0), false, Defaults);

        // Assert
        verdict.ShouldBe(new TranslationSaveBudgetVerdict(null, false, false));
    }
}
