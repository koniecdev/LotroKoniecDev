using LotroKoniecDev.SharedKernel.Monads;
using LotroKoniecDev.Tests.Shared;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;

namespace LotroKoniecDev.TranslationSystem.Domain.Tests.Unit.Tests;

/// <summary>
/// Pins that every string value object answers the whole Big List of Naughty Strings with a
/// <c>Result</c>, never an exception (#569): each is fed from something a stranger controls, and a
/// factory that throws turns a 400 into a 500.
/// </summary>
public sealed class NaughtyStringValueObjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void LotroNotationVersionCreate_NaughtyInput_ShouldAnswerWithAResultInsteadOfThrowing(string naughty)
    {
        // Act & Assert: most entries are rejected; none may throw.
        Should.NotThrow(() => LotroNotationVersion.Create(naughty));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void DisplayNameCreate_NaughtyInput_ShouldAnswerWithAResultInsteadOfThrowing(string naughty)
    {
        // Act & Assert
        Should.NotThrow(() => DisplayName.Create(naughty));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void EmailCreate_NaughtyInput_ShouldAnswerWithAResultInsteadOfThrowing(string naughty)
    {
        // Act & Assert: a regex, the classic place for hostile input to blow up.
        Should.NotThrow(() => Email.Create(naughty));
    }

    /// <summary>
    /// Filtered here, not inside the test, so the assertion stays unconditional: a change that rejected
    /// everything fails instead of passing untested.
    /// </summary>
    public static TheoryData<string> AcceptableDisplayNames
    {
        get
        {
            TheoryData<string> data = [];

            foreach (string candidate in NaughtyStringCases.AllValues)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && candidate.Trim().Length <= DisplayName.MaxLength)
                {
                    data.Add(candidate);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AcceptableDisplayNames))]
    public void DisplayNameCreate_NaughtyInputWithinTheRules_ShouldSucceedAndKeepItTrimmedButVerbatim(string naughty)
    {
        // Act
        Result<DisplayName> result = DisplayName.Create(naughty);

        // Assert: acceptance never mangles the value, because other translators see it as stored.
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(naughty.Trim());
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void TranslationSourceCreate_NaughtyText_ShouldStoreItVerbatim(string naughty)
    {
        // Act: any normalisation would read as a source change and mass-invalidate Polish rows.
        Result<TranslationSource> result = TranslationSource.Create(naughty, null, null);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldBe(naughty);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public void TranslationSourceEquality_NaughtyTextDifferingByAnInvisibleCodePoint_ShouldNotBeEqual(string naughty)
    {
        // Arrange: the diff compares these values, so equality must stay exact: a zero-width code point
        // makes different English even when it renders the same.
        TranslationSource source = TranslationSource.Create(naughty, null, null).Value;
        TranslationSource sameText = TranslationSource.Create(naughty, null, null).Value;
        TranslationSource withZeroWidthSpace = TranslationSource.Create($"{naughty}\u200B", null, null).Value;

        // Assert
        source.ShouldBe(sameText);
        source.ShouldNotBe(withZeroWidthSpace);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.SubmittableText), MemberType = typeof(NaughtyStringCases))]
    public void ProvideTranslation_NaughtyPolishAroundAPlaceholder_ShouldStoreItVerbatim(string naughty)
    {
        // Arrange: the Polish ships unchanged, and the <--DO_NOT_TOUCH!--> marker must survive hostile
        // text around it, or the fragment loses its arguments in-game.
        Translation translation = Translation.CreateUntranslated(
            FragmentKey.Create(620756992, 1001).Value,
            TranslationSource.Create("Old English", null, null).Value,
            GameVersionId.Create(),
            Now).Value;

        string polish = $"{naughty}<--DO_NOT_TOUCH!-->{naughty}";

        // Act
        translation.ProvideTranslation(polish, TranslatorId.Create(), Now);

        // Assert
        translation.TranslatedText.ShouldBe(polish);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.BlankText), MemberType = typeof(NaughtyStringCases))]
    public void ProvideTranslation_BlankPolish_ShouldThrowBecauseTheApiMustHaveRejectedItFirst(string blank)
    {
        // Arrange: a programmer-error guard, since the validator already answers 400. Pinned so it is
        // never relaxed into publishing an empty row.
        Translation translation = Translation.CreateUntranslated(
            FragmentKey.Create(620756992, 1001).Value,
            TranslationSource.Create("Old English", null, null).Value,
            GameVersionId.Create(),
            Now).Value;

        // Act
        Action provide = () => translation.ProvideTranslation(blank, TranslatorId.Create(), Now);

        // Assert
        provide.ShouldThrow<ArgumentException>();
    }
}
