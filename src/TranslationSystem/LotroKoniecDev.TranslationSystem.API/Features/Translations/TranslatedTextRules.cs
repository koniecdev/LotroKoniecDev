using FluentValidation;
using LotroKoniecDev.TranslationSystem.Primitives.Constants;

namespace LotroKoniecDev.TranslationSystem.API.Features.Translations;

/// <summary>
/// The rule of every write of a typed Polish text, so the save and the copy into identical rows cannot drift
/// apart (#1587). Above the DAT limit the patcher cannot write the row at all (#598, ADR-0043), so the
/// translator is told here, not a player halfway through a patch.
/// </summary>
internal static class TranslatedTextRules
{
    public static IRuleBuilderOptions<T, string> MustBeAWritableTranslation<T>(this IRuleBuilder<T, string> ruleBuilder) =>
        ruleBuilder
            .NotEmpty()
            .MaximumLength(DatFormatConstants.MaxTranslatedTextLength);
}
