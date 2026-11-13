using FluentValidation;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// A zero limit would refuse every save, and an alert threshold above its limit would switch the alert
/// off with no sign of it, so the boot stops (ADR-0008 §3).
/// </summary>
internal sealed class TranslationSaveBudgetSettingsValidator : AbstractValidator<TranslationSaveBudgetSettings>
{
    public TranslationSaveBudgetSettingsValidator()
    {
        RuleFor(settings => settings.SaveLimit)
            .GreaterThanOrEqualTo(1)
            .WithMessage($"{KeyPath(nameof(TranslationSaveBudgetSettings.SaveLimit))} must be at least 1.");

        RuleFor(settings => settings.SaveAlertThreshold)
            .GreaterThanOrEqualTo(1)
            .WithMessage(ThresholdMessage(nameof(TranslationSaveBudgetSettings.SaveAlertThreshold), nameof(TranslationSaveBudgetSettings.SaveLimit)))
            .LessThanOrEqualTo(settings => settings.SaveLimit)
            .WithMessage(ThresholdMessage(nameof(TranslationSaveBudgetSettings.SaveAlertThreshold), nameof(TranslationSaveBudgetSettings.SaveLimit)));

        RuleFor(settings => settings.OverwriteLimit)
            .GreaterThanOrEqualTo(1)
            .WithMessage($"{KeyPath(nameof(TranslationSaveBudgetSettings.OverwriteLimit))} must be at least 1.");

        RuleFor(settings => settings.OverwriteAlertThreshold)
            .GreaterThanOrEqualTo(1)
            .WithMessage(ThresholdMessage(nameof(TranslationSaveBudgetSettings.OverwriteAlertThreshold), nameof(TranslationSaveBudgetSettings.OverwriteLimit)))
            .LessThanOrEqualTo(settings => settings.OverwriteLimit)
            .WithMessage(ThresholdMessage(nameof(TranslationSaveBudgetSettings.OverwriteAlertThreshold), nameof(TranslationSaveBudgetSettings.OverwriteLimit)))
            // Every overwrite is also a save, so the overwrite count never passes the save limit.
            .LessThanOrEqualTo(settings => settings.SaveLimit)
            .WithMessage(
                $"{KeyPath(nameof(TranslationSaveBudgetSettings.OverwriteAlertThreshold))} must not be above "
                + $"{KeyPath(nameof(TranslationSaveBudgetSettings.SaveLimit))}: an overwrite is also a save, so the alert could never fire.");

        RuleFor(settings => settings.WindowHours)
            .InclusiveBetween(1, TranslationSaveBudgetSettings.MaxWindowHours)
            .WithMessage(
                $"{KeyPath(nameof(TranslationSaveBudgetSettings.WindowHours))} must be between 1 and "
                + $"{TranslationSaveBudgetSettings.MaxWindowHours}.");
    }

    private static string KeyPath(string propertyName)
        => $"{TranslationSaveBudgetSettings.ConfigurationSection}:{propertyName}";

    private static string ThresholdMessage(string thresholdName, string limitName)
        => $"{KeyPath(thresholdName)} must be between 1 and {KeyPath(limitName)}.";
}
