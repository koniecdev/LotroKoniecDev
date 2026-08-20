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
/// Hostile-input coverage for the value objects that constrain a string (#569). Every one of them is fed
/// from something a stranger controls: an uploaded export, an OIDC claim or a form post. So the house
/// rule that business failures are values and exceptions are for programmer errors has to hold for the
/// whole Big List of Naughty Strings and not only for the cases each value object's own suite picked. A
/// factory that throws turns a 400 into a 500.
/// </summary>
public sealed class NaughtyStringValueObjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void LotroNotationVersionCreate_NaughtyInput_ShouldAnswerWithAResultInsteadOfThrowing(string naughty)
    {
        // Act & Assert: a rejection is the expected outcome for almost every entry; what must never
        // happen is an exception escaping the factory.
        Should.NotThrow(() => LotroNotationVersion.Create(naughty));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void DisplayNameCreate_NaughtyInput_ShouldAnswerWithAResultInsteadOfThrowing(string naughty)
    {
        // Act & Assert: the display name is lifted straight from the authenticated 'name' claim.
        Should.NotThrow(() => DisplayName.Create(naughty));
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void EmailCreate_NaughtyInput_ShouldAnswerWithAResultInsteadOfThrowing(string naughty)
    {
        // Act & Assert: the email is lifted straight from the authenticated 'email' claim and is
        // matched against a regex, the classic place for hostile input to blow up.
        Should.NotThrow(() => Email.Create(naughty));
    }

    /// <summary>
    /// The naughty strings a display name may legitimately be: not blank and within the length limit.
    /// Filtering here instead of branching inside the test keeps the assertion unconditional, so a change
    /// that rejected everything would fail it instead of passing without testing anything.
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

        // Assert: the blankness and length rules are DisplayNameTests' job; what this pins is that
        // acceptance never mangles the value: no normalisation, no stripping, no case folding,
        // because the string is rendered back to other translators exactly as stored.
        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(naughty.Trim());
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.All), MemberType = typeof(NaughtyStringCases))]
    public void TranslationSourceCreate_NaughtyText_ShouldStoreItVerbatim(string naughty)
    {
        // Act: English source text is exported verbatim from the DAT, so the VO must not touch it:
        // any normalisation here would read as a source change and mass-invalidate Polish rows.
        Result<TranslationSource> result = TranslationSource.Create(naughty, null, null);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.Text.ShouldBe(naughty);
    }

    [Theory]
    [MemberData(nameof(NaughtyStringCases.UnicodeHazards), MemberType = typeof(NaughtyStringCases))]
    public void TranslationSourceEquality_NaughtyTextDifferingByAnInvisibleCodePoint_ShouldNotBeEqual(string naughty)
    {
        // Arrange: the import diff decides invalidation by comparing these values, so equality has
        // to stay exact: two sources that RENDER identically but differ by a zero-width code point
        // are still different English, and collapsing them would hide a real source change.
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
        // Arrange: whatever a translator types lands in the distributed file unchanged, so the
        // aggregate must not normalise it, and the <--DO_NOT_TOUCH!--> argument marker must survive
        // being surrounded by hostile text: mangling it detaches the fragment's arguments in-game.
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
        // Arrange: this guard is for a PROGRAMMER error, not a business rule: the upsert slice's
        // FluentValidation NotEmpty() already turns blank Polish into a 400, so reaching the
        // aggregate with it means the validator was bypassed. Pinned so the guard is not quietly
        // relaxed into accepting a translation that would publish an empty row.
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
