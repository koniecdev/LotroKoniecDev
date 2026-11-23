using LotroKoniecDev.Frontend.Components.Pages.Editor;

namespace LotroKoniecDev.Frontend.Tests.Unit.Components.Pages.Editor;

public sealed class TranslationSlipCheckerTests
{
    private const string Token = "<--DO_NOT_TOUCH!-->";

    /// <summary>The game's line break: two typed characters, not a real LF.</summary>
    private const string Br = @"\n";

    private const string GameQuote = @"\q";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t ")]
    [InlineData("\r\n")]
    [InlineData("\n\n")]
    public void Check_WhenThePolishIsMissingOrBlank_FindsNoSlip(string? translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check("'Hello.' [D]", translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Plain English line.", "Zwykły polski wiersz.")]
    [InlineData("Zażółć gęślą jaźń?", "Zażółć gęślą jaźń?")]
    [InlineData("You have " + Token + " of " + Token + " items.", "Masz " + Token + " z " + Token + " przedmiotów.")]
    [InlineData(Token + " gained " + Token, Token + " zdobywa " + Token)]
    [InlineData("Hello " + Token + "!", "Cześć " + Token + "!")]
    [InlineData("", "Tekst bez znaku na końcu")]
    [InlineData("Hello?!", "Cześć!")]
    [InlineData("Line one" + Br + "Line two.", "Wiersz pierwszy" + Br + "Wiersz drugi.")]
    public void Check_WhenThePolishMatchesTheEnglishOnEveryPoint_FindsNoSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("'The Age of the Elves is over.'", "'Czas elfów dobiegł końca.'")]
    [InlineData("'First we rest." + Br + Br + "'Then we ride to Bree.'", "'Najpierw odpoczniemy." + Br + Br + "'Potem ruszymy do Bree.'")]
    [InlineData("Strider says, 'Get set...'", "Obieżyświat mówi: 'Uwaga...'")]
    [InlineData("Find the book 'Tales of the North'", "Znajdź książkę „Opowieści z Północy”")]
    public void Check_TheExamplesOfTheWikiPageOnSpeechAndQuotes_FindsNoSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Hello.", "Cześć.\r\n")]
    [InlineData("Hello.", "Cześć.\n\n")]
    [InlineData("Hello.", "Cześć.\r")]
    public void Check_WhenThePolishEndsWithAnEnterTheSaveRemoves_FindsNoSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Fact]
    public void Check_WhenTheEnterTheSaveRemovesHidesASpace_StillFindsTheSpace()
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check("Hello.", "Cześć. \r\n");

        slips.Select(slip => slip.Kind).ShouldBe([TranslationSlipKind.BlankAtEnd]);
    }

    [Theory]
    [InlineData("Hello.", " Cześć.")]
    [InlineData("Hello.", "\tCześć.")]
    [InlineData(" Hello.", "Cześć.")]
    [InlineData("\tHello.", "Cześć.")]
    public void Check_WhenOnlyOneSideStartsWithABlank_FindsABlankAtTheStart(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Select(slip => slip.Kind).ShouldBe([TranslationSlipKind.BlankAtStart]);
    }

    [Theory]
    [InlineData("Hello.", "Cześć. ")]
    [InlineData("Hello.", "Cześć.\t")]
    [InlineData("Hello. ", "Cześć.")]
    [InlineData("Hello.\t", "Cześć.")]
    [InlineData("Hello.", "Cześć.  ")]
    public void Check_WhenOnlyOneSideEndsWithABlank_FindsABlankAtTheEnd(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Select(slip => slip.Kind).ShouldBe([TranslationSlipKind.BlankAtEnd]);
    }

    [Theory]
    [InlineData(" Hello. ", " Cześć. ")]
    [InlineData("\tHello.\t", " Cześć. ")]
    [InlineData(" Hello.", "\tCześć.")]
    public void Check_WhenBothSidesHaveABlankAtTheSameEdge_FindsNoSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Hello.", " Cześć.", "Na początku tłumaczenia jest spacja, a w angielskim nie ma.")]
    [InlineData("\tHello.", "Cześć.", "Na początku tekstu angielskiego jest tabulator, a w tłumaczeniu nie ma.")]
    [InlineData("Hello.", "Cześć.\t", "Na końcu tłumaczenia jest tabulator, a w angielskim nie ma.")]
    [InlineData("Hello. ", "Cześć.", "Na końcu tekstu angielskiego jest spacja, a w tłumaczeniu nie ma.")]
    public void Check_ABlankAtAnEdge_NamesTheSideAndTheCharacter(string sourceText, string translatedText, string expected)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Single().Message.ShouldBe(expected);
    }

    [Fact]
    public void Check_WhenThePolishHasBlanksAtBothEdges_FindsOneSlipForEachEdge()
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check("Hello.", " Cześć. ");

        slips.Select(slip => slip.Kind).ShouldBe([TranslationSlipKind.BlankAtStart, TranslationSlipKind.BlankAtEnd]);
    }

    [Theory]
    [InlineData("Hello there.", "Cześć  wam.")]
    [InlineData("Hello there.", "Cześć   wam.")]
    [InlineData("Hello. ", "Cześć.  ")]
    [InlineData(" Hello.", "  Cześć.")]
    [InlineData("Hello.", " Cześć  wam.")]
    public void Check_WhenThePolishHasTwoSpacesInARowAndTheEnglishHasNone_FindsADoubleSpace(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Select(slip => slip.Kind).ShouldContain(TranslationSlipKind.DoubleSpace);
    }

    [Theory]
    [InlineData("Hello  there.", "Cześć  wam.")]
    [InlineData("Hello there.", "Cześć \t wam.")]
    [InlineData("Hello there.", "Cześć wam.")]
    public void Check_WhenTheEnglishAlsoHasTwoSpacesOrThePolishHasNone_FindsNoDoubleSpace(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Hello.", "Cześć.  ")]
    [InlineData("Hello.", "  Cześć.")]
    public void Check_WhenTheTwoSpacesAreAnEdgeTheBlankCheckReports_GivesOnlyThatOneLine(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Select(slip => slip.Kind).ShouldNotContain(TranslationSlipKind.DoubleSpace);
        slips.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("Read the book.", "Przeczytaj \"Księgę\".")]
    [InlineData("Read the book.", "Przeczytaj \"Księgę\" i \"Kronikę\".")]
    public void Check_WhenThePolishHasAStraightDoubleQuote_FindsOneQuoteSlipWithTheSpeechRule(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        TranslationSlip slip = slips.ShouldHaveSingleItem();
        slip.Kind.ShouldBe(TranslationSlipKind.StraightQuote);
        slip.Message.ShouldBe("W tekstach gry nie ma znaku \". Mowa postaci zostaje w apostrofach ', inne cytaty piszemy w „…”.");
    }

    [Theory]
    [InlineData("Line one" + Br + "Line two.", "Wiersz pierwszy, wiersz drugi.")]
    [InlineData("One line.", "Wiersz" + Br + Br + "drugi.")]
    [InlineData("Line one" + Br + "Line two.", "Wiersz pierwszy\nWiersz drugi.")]
    [InlineData("Line one" + Br + "Line two.", "Wiersz pierwszy\r\nWiersz drugi.")]
    public void Check_WhenTheCountOfTypedLineBreaksDiffers_FindsALineBreakSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Select(slip => slip.Kind).ShouldBe([TranslationSlipKind.LineBreakCount]);
    }

    [Fact]
    public void Check_ALineBreakSlip_GivesBothCountsAndSaysItIsNotEnter()
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check("A" + Br + "B" + Br + "C.", "A" + Br + "B.");

        slips.Single().Message.ShouldBe(@"Inna liczba \n niż w angielskim (1 zamiast 2). Liczą się dwa wpisane znaki \n, nie Enter.");
    }

    [Theory]
    [InlineData("Stout-axe[D]", "Topór wojenny", "Tekst angielski kończy się znacznikiem [D], a tłumaczenie nie.")]
    [InlineData("Stout-axe[D]", "Topór wojenny[m]", "Tłumaczenie kończy się znacznikiem [m], a tekst angielski znacznikiem [D].")]
    [InlineData("Necklace", "Naszyjnik[E]", "Tłumaczenie kończy się znacznikiem [E], a tekst angielski nie ma znacznika.")]
    [InlineData("Necklace[E]", "Naszyjnik[e]", "Tłumaczenie kończy się znacznikiem [e], a tekst angielski znacznikiem [E].")]
    [InlineData("Ring [Pl] ", "Pierścień [Plf] ", "Tłumaczenie kończy się znacznikiem [Plf], a tekst angielski znacznikiem [Pl].")]
    public void Check_WhenTheGrammarMarkAtTheEndDiffers_FindsAMarkSlipNamingBothMarks(string sourceText, string translatedText, string expected)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        TranslationSlip slip = slips.ShouldHaveSingleItem();
        slip.Kind.ShouldBe(TranslationSlipKind.GrammarMark);
        slip.Message.ShouldBe(expected);
    }

    [Theory]
    [InlineData("Stout-axe[D]", "Topór wojenny[D]")]
    [InlineData("Stout-axe[D]", "Topór wojenny [D]")]
    [InlineData("Stout-axe [D] ", "Topór wojenny [D] ")]
    [InlineData("Stout-axe [D]\t", "Topór wojenny[D]\t")]
    [InlineData("[E]", "[E]")]
    [InlineData("Necklace[E]", "[E]")]
    [InlineData("Hello[ABCDE]", "Cześć")]
    [InlineData("Sword[D]" + Br, "Miecz" + Br)]
    public void Check_WhenTheGrammarMarksAgreeOrNeitherSideHasOne_FindsNoSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("'Hello.'", "Cześć.'")]
    [InlineData("'Hello.'", "„Cześć.”")]
    [InlineData("'Hello.'", GameQuote + "Cześć." + GameQuote)]
    [InlineData(" 'Hello.'", " Cześć.'")]
    public void Check_WhenTheEnglishStartsWithAnApostropheAndThePolishDoesNot_FindsAnOpeningSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        TranslationSlip slip = slips.ShouldHaveSingleItem();
        slip.Kind.ShouldBe(TranslationSlipKind.OpeningApostrophe);
        slip.Message.ShouldBe("Tekst angielski zaczyna się apostrofem ', a tłumaczenie nie.");
    }

    [Theory]
    [InlineData("'Hello.'", "'Cześć.'")]
    [InlineData(" 'Hello.'", " 'Cześć.'")]
    [InlineData("Hello.", "'Cześć.'")]
    [InlineData("He said 'Hello.'", "Powiedział „Cześć.”")]
    [InlineData("He said 'Hello.'", "Powiedział " + GameQuote + "Cześć." + GameQuote)]
    [InlineData("'Hello.'" + Br, "'Cześć.'" + Br)]
    [InlineData("'Hello.' [m]", "'Cześć.' [m]")]
    public void Check_WhenTheQuotesAgreeOrTheEnglishHasNone_FindsNoSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("'Hello.'", "'Cześć.")]
    [InlineData("Strider says, 'Get set...'", "Obieżyświat mówi: 'Uwaga...")]
    [InlineData("'Hello.' [m]", "'Cześć. [m]")]
    [InlineData("'Hello.'" + Br + Br, "'Cześć." + Br + Br)]
    public void Check_WhenTheEnglishEndsWithAnApostropheAndThePolishHasNoClosingQuote_FindsAClosingSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        TranslationSlip slip = slips.ShouldHaveSingleItem();
        slip.Kind.ShouldBe(TranslationSlipKind.ClosingQuote);
        slip.Message.ShouldBe(@"Tekst angielski kończy się apostrofem ', a tłumaczenie nie kończy się ani ', ani ”, ani \q.");
    }

    [Fact]
    public void Check_WhenThePolishClosesWithACurlyApostrophe_FindsTheClosingSlipAndATrueFinalSignLine()
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check("'Hello.'", "'Cześć.’");

        slips.Select(slip => slip.Kind).ShouldBe([TranslationSlipKind.ClosingQuote, TranslationSlipKind.FinalSign]);
        slips[1].Message.ShouldBe("Inny znak na końcu niż w angielskim: w angielskim kropka (.), w tłumaczeniu brak znaku (na końcu jest ’).");
    }

    [Fact]
    public void Check_WhenSpeechIsInStraightDoubleQuotes_FindsTheQuoteBothApostrophesAndTheFinalSign()
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check("'Hello.'", "\"Cześć.\"");

        slips.Select(slip => slip.Kind).ShouldBe(
        [
            TranslationSlipKind.StraightQuote,
            TranslationSlipKind.OpeningApostrophe,
            TranslationSlipKind.ClosingQuote,
            TranslationSlipKind.FinalSign
        ]);
    }

    [Theory]
    [InlineData("Hello.", "Cześć")]
    [InlineData("Hello", "Cześć.")]
    [InlineData("Hello!", "Cześć.")]
    [InlineData("Hello?", "Cześć!")]
    [InlineData("Wait:", "Czekaj")]
    [InlineData("Wait...", "Czekaj.")]
    [InlineData("Wait…", "Czekaj!")]
    [InlineData("'Hello.'", "'Cześć'")]
    [InlineData("Hello. [D]", "Cześć [D]")]
    [InlineData("Hello." + Br, "Cześć" + Br)]
    public void Check_WhenTheLastSignBeforeTheMarkDiffers_FindsAFinalSignSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Select(slip => slip.Kind).ShouldBe([TranslationSlipKind.FinalSign]);
    }

    [Theory]
    [InlineData("Wait...", "Czekaj…")]
    [InlineData("Wait…", "Czekaj....")]
    [InlineData("Wait..", "Czekaj.")]
    [InlineData("Wait.", "Czekaj..")]
    [InlineData("Hello", "Cześć")]
    [InlineData("Hello." + Br + Br, "Cześć." + Br + Br)]
    [InlineData("'Hello.'", "'Cześć.'")]
    [InlineData("He said 'Hello!'", "Powiedział „Cześć!”")]
    public void Check_WhenTheLastSignsAgree_FindsNoSlip(string sourceText, string translatedText)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Hello.", "Cześć", "Inny znak na końcu niż w angielskim: w angielskim kropka (.), w tłumaczeniu brak znaku.")]
    [InlineData("Wait...", "Czekaj?", "Inny znak na końcu niż w angielskim: w angielskim wielokropek (…), w tłumaczeniu znak zapytania (?).")]
    [InlineData("Go:", "Idź!", "Inny znak na końcu niż w angielskim: w angielskim dwukropek (:), w tłumaczeniu wykrzyknik (!).")]
    [InlineData("You have " + Token, "Masz " + Token + ".", "Inny znak na końcu niż w angielskim: w angielskim brak znaku (na końcu jest >), w tłumaczeniu kropka (.).")]
    [InlineData("[D]", "Tekst.[D]", "Inny znak na końcu niż w angielskim: w angielskim brak znaku, w tłumaczeniu kropka (.).")]
    public void Check_AFinalSignSlip_NamesWhatEachSideEndsWith(string sourceText, string translatedText, string expected)
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(sourceText, translatedText);

        slips.Single().Message.ShouldBe(expected);
    }

    [Fact]
    public void Check_WhenEverySlipIsThere_ListsOneSlipPerCheckInCheckOrder()
    {
        IReadOnlyList<TranslationSlip> slips = TranslationSlipChecker.Check(
            "'One" + Br + "two.'[D]",
            " \"Jeden  dwa\" ");

        slips.Select(slip => slip.Kind).ShouldBe(
        [
            TranslationSlipKind.BlankAtStart,
            TranslationSlipKind.BlankAtEnd,
            TranslationSlipKind.DoubleSpace,
            TranslationSlipKind.StraightQuote,
            TranslationSlipKind.LineBreakCount,
            TranslationSlipKind.GrammarMark,
            TranslationSlipKind.OpeningApostrophe,
            TranslationSlipKind.ClosingQuote,
            TranslationSlipKind.FinalSign
        ]);
    }
}
