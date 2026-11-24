using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.StronglyTypedIds;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Translations;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.GameVersionAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslationAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.Entities;
using LotroKoniecDev.TranslationSystem.Domain.Aggregates.TranslatorAggregate.ValueObjects;
using LotroKoniecDev.TranslationSystem.Persistence.DbContexts.WriteDbContexts;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslatorAggregate;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Integration.Tests.Translations;

[Collection("TranslationApi")]
public sealed class ListTranslationsTests : IAsyncLifetime
{
    private const int FileId = 620756992;
    private const int OtherFileId = 620756993;
    private const int ThirdFileId = 620756994;
    private const string SeederDisplayName = "Seed Author";
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TranslationSystemApiFactory _factory;
    private GameVersionId _versionId;
    private TranslatorId _seederId;

    public ListTranslationsTests(TranslationSystemApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync(
            "TRUNCATE translation.\"Translations\", translation.\"GameVersions\", translation.\"Translators\" CASCADE;");

        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();

        GameVersion gameVersion = GameVersion.Create(LotroNotationVersion.Create("48.0").Value, Now).Value;
        dbContext.GameVersions.Add(gameVersion);

        Translator seeder = Translator.Create(
            IdentityId.Create(), DisplayName.Create(SeederDisplayName).Value, email: null, Now).Value;
        dbContext.Translators.Add(seeder);

        await dbContext.SaveChangesAsync();
        _versionId = gameVersion.Id;
        _seederId = seeder.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task List_ShouldReturnRowsSortedByFileIdThenGossipId()
    {
        // Arrange: seeded out of order, across two file ids.
        await SeedAsync(Row(1, "B", fileId: 200), Row(2, "A", fileId: 100), Row(1, "C", fileId: 100));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?page=1&pageSize=10");

        // Assert
        page.TotalCount.ShouldBe(3);
        page.Items.Select(item => (item.FileId, item.GossipId))
            .ShouldBe([(100, 1L), (100, 2L), (200, 1L)]);
    }

    [Fact]
    public async Task List_ShouldPaginate()
    {
        // Arrange: five rows, two per page.
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"), Row(4, "d"), Row(5, "e"));

        // Act
        PaginationResponse<TranslationListItemResponse> firstPage = await ListAsync("?page=1&pageSize=2");
        PaginationResponse<TranslationListItemResponse> lastPage = await ListAsync("?page=3&pageSize=2");

        // Assert
        firstPage.TotalCount.ShouldBe(5);
        firstPage.Items.Count.ShouldBe(2);
        firstPage.HasNextPage.ShouldBeTrue();
        firstPage.HasPreviousPage.ShouldBeFalse();

        lastPage.Items.Count.ShouldBe(1);
        lastPage.Items.Single().GossipId.ShouldBe(5);
        lastPage.HasNextPage.ShouldBeFalse();
        lastPage.HasPreviousPage.ShouldBeTrue();
    }

    [Fact]
    public async Task List_WithPageBeyondRange_ShouldReturnEmptyWithAccurateTotal()
    {
        // Arrange
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?page=99&pageSize=2");

        // Assert
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task List_WithSearch_ShouldMatchContentCaseInsensitively()
    {
        // Arrange
        await SeedAsync(Row(1, "Frodo Baggins"), Row(2, "Samwise Gamgee"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?search=FRODO");

        // Assert
        page.Items.Single().GossipId.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithSearch_ShouldAlsoMatchTheTranslatedText()
    {
        // Arrange: only the drafted row carries Polish ("Polski tekst").
        await SeedAsync(Row(1, "Aragorn"), Row(2, "Boromir", TranslationStatus.Draft));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?search=tekst");

        // Assert: the list item joins the submitter's display name (ADR-0004).
        TranslationListItemResponse item = page.Items.Single();
        item.GossipId.ShouldBe(2);
        item.Submitter.ShouldNotBeNull();
        item.Submitter.DisplayName.ShouldBe(SeederDisplayName);
    }

    [Fact]
    public async Task List_WithLikeMetacharacterInSearch_ShouldMatchItLiterally()
    {
        // Arrange: '%' must be a literal, not a LIKE wildcard, or "100%" would also match "1000".
        await SeedAsync(Row(1, "Reward 100% bonus"), Row(2, "Reward 1000 bonus"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?search=100%25");

        // Assert
        page.Items.Single().GossipId.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithAFileIdAsSearch_ShouldListEveryLiveRowOfThatFile()
    {
        // Arrange
        await SeedAsync(
            Row(1, "Frodo"),
            Row(2, "Samwise"),
            Row(3, "Gone", removed: true),
            Row(1, "Gandalf", fileId: OtherFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync(new ListTranslationsRequest(Search: FileId.ToString(CultureInfo.InvariantCulture)));

        // Assert
        page.TotalCount.ShouldBe(2);
        page.Items.Select(item => (item.FileId, item.GossipId)).ShouldBe([(FileId, 1L), (FileId, 2L)]);
    }

    [Fact]
    public async Task List_WithAGossipIdAsSearch_ShouldListItsRowsInEveryFile()
    {
        // Arrange
        await SeedAsync(
            Row(6396484, "Frodo"),
            Row(6396485, "Samwise"),
            Row(6396484, "Gandalf", fileId: OtherFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync(new ListTranslationsRequest(Search: "6396484"));

        // Assert
        page.Items.Select(item => (item.FileId, item.GossipId))
            .ShouldBe([(FileId, 6396484L), (OtherFileId, 6396484L)]);
    }

    [Fact]
    public async Task List_WithAGossipIdTooBigForAnInt_ShouldStillMatchIt()
    {
        // Arrange: FileId is an int, so only the GossipId column can hold this number.
        await SeedAsync(Row(2147483648L, "Frodo"), Row(1, "Samwise"));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync(new ListTranslationsRequest(Search: "2147483648"));

        // Assert
        page.Items.ShouldHaveSingleItem().GossipId.ShouldBe(2147483648L);
    }

    [Fact]
    public async Task List_WithANumberThatIsAlsoInAText_ShouldListTheIdMatchAndTheTextMatch()
    {
        // Arrange: game texts carry numbers too, so an id match must never hide a text match.
        await SeedAsync(Row(100, "Kill wolves"), Row(200, "Reward: 100 silver"), Row(300, "Nothing here"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync(new ListTranslationsRequest(Search: "100"));

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([100L, 200L]);
    }

    [Fact]
    public async Task List_WithANumberPaddedWithSpaces_ShouldTrimItAndMatchTheId()
    {
        // Arrange
        await SeedAsync(Row(6396484, "Frodo"), Row(6396485, "Samwise"));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync(new ListTranslationsRequest(Search: "  6396484 "));

        // Assert
        page.Items.ShouldHaveSingleItem().GossipId.ShouldBe(6396484L);
    }

    [Fact]
    public async Task List_WithFileIdColonGossipId_ShouldListOnlyThatRow()
    {
        // Arrange: one row shares the FileId and one shares the GossipId, so an OR of the two parts fails.
        await SeedAsync(
            Row(6396484, "Frodo"),
            Row(6396485, "Samwise"),
            Row(6396484, "Gandalf", fileId: OtherFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync(new ListTranslationsRequest(Search: $"{FileId}:6396484"));

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        (item.FileId, item.GossipId).ShouldBe((FileId, 6396484L));
    }

    [Fact]
    public async Task List_WithFileIdColonGossipIdOfNoRow_ShouldListOnlyTheTextMatches()
    {
        // Arrange: no row has the pair, one row has only its FileId, one only its GossipId.
        await SeedAsync(
            Row(5, "Frodo"),
            Row(9, "Gandalf", fileId: OtherFileId),
            Row(7, $"See {FileId}:9 for details", fileId: ThirdFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync(new ListTranslationsRequest(Search: $"{FileId}:9"));

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        (item.FileId, item.GossipId).ShouldBe((ThirdFileId, 7L));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("620756992:3")]
    public async Task List_WithTheIdOfARemovedRow_ShouldNotListIt(string term)
    {
        // Arrange
        await SeedAsync(Row(1, "Frodo"), Row(3, "Gone", removed: true));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync(new ListTranslationsRequest(Search: term));

        // Assert
        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_WithAnIdAndAStatus_ShouldApplyTheStatusOnTopOfTheIdMatch()
    {
        // Arrange
        await SeedAsync(
            Row(1, "Frodo", TranslationStatus.Draft),
            Row(2, "Samwise"),
            Row(3, "Gandalf", TranslationStatus.Draft, fileId: OtherFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync(new ListTranslationsRequest(
            Search: FileId.ToString(CultureInfo.InvariantCulture),
            Status: TranslationStatus.Draft));

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        (item.FileId, item.GossipId).ShouldBe((FileId, 1L));
    }

    [Fact]
    public async Task List_WithAnIdAndConflictsOnly_ShouldApplyTheConflictFilterOnTopOfTheIdMatch()
    {
        // Arrange: the conflict group spans two files; one row of the searched file has no conflict.
        await SeedAsync(
            Translated(1, "Stout-axe[D]", "Topór wojenny"),
            Translated(2, "Stout-axe[D]", "Krzepki Topornik"),
            Translated(3, "Dwarf[D]", "Krasnolud"),
            Translated(1, "Stout-axe[D]", "Wytrzymały topór", fileId: OtherFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync(new ListTranslationsRequest(
            Search: FileId.ToString(CultureInfo.InvariantCulture),
            ConflictsOnly: true));

        // Assert
        page.Items.Select(item => (item.FileId, item.GossipId)).ShouldBe([(FileId, 1L), (FileId, 2L)]);
    }

    [Theory]
    [MemberData(nameof(TextOnlyTerms))]
    public async Task List_WithATermThatIsNeitherAPlainNumberNorTheColonForm_ShouldOnlySearchTheText(
        string term,
        int decoyFileId,
        long decoyGossipId)
    {
        // Arrange: the decoy carries the ids a lenient parse would read from the term.
        await SeedAsync(
            Row(1, $"Quest {term} text", fileId: ThirdFileId),
            Row(decoyGossipId, "Decoy", fileId: decoyFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync(new ListTranslationsRequest(Search: term));

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        (item.FileId, item.GossipId).ShouldBe((ThirdFileId, 1L));
    }

    [Fact]
    public async Task List_WithStatusNeedsReview_ShouldReturnOnlyInvalidatedRows()
    {
        // Arrange: one of each status; NeedsReview is the "needs re-translation" view.
        await SeedAsync(
            Row(1, "untranslated"),
            Row(2, "drafted", TranslationStatus.Draft),
            Row(3, "invalidated", TranslationStatus.NeedsReview));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?status=NeedsReview");

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        item.GossipId.ShouldBe(3);
        item.Status.ShouldBe(TranslationStatus.NeedsReview);
    }

    [Fact]
    public async Task List_ShouldHideSoftRemovedRowsByDefault()
    {
        // Arrange
        await SeedAsync(Row(1, "kept"), Row(2, "removed", removed: true));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?page=1&pageSize=10");

        // Assert
        page.TotalCount.ShouldBe(1);
        page.Items.Single().GossipId.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithSortGossipIdDescending_ReturnsRowsDescending()
    {
        // Arrange: seeded ascending; the descending sort must reverse them.
        await SeedAsync(Row(1, "a"), Row(2, "b"), Row(3, "c"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?sort=gossipId:desc");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([3L, 2L, 1L]);
    }

    [Fact]
    public async Task List_WithMultiKeySort_AppliesStatusDescThenFileIdAsc()
    {
        // Arrange: Status is stored as its name, so "desc" puts NeedsReview before Draft.
        await SeedAsync(
            Row(1, "needs", TranslationStatus.NeedsReview, fileId: 300),
            Row(1, "draft-hi", TranslationStatus.Draft, fileId: 200),
            Row(1, "draft-lo", TranslationStatus.Draft, fileId: 100));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?sort=status:desc,fileId:asc");

        // Assert
        page.Items.Select(item => (item.FileId, item.Status)).ShouldBe(
        [
            (300, TranslationStatus.NeedsReview),
            (100, TranslationStatus.Draft),
            (200, TranslationStatus.Draft)
        ]);
    }

    [Fact]
    public async Task List_WithUnknownSortKey_FallsBackToDefaultOrdering()
    {
        // Arrange: seeded out of FileId order.
        await SeedAsync(Row(1, "b", fileId: 200), Row(1, "a", fileId: 100), Row(1, "c", fileId: 300));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?sort=banana");

        // Assert
        page.Items.Select(item => item.FileId).ShouldBe([100, 200, 300]);
    }

    [Fact]
    public async Task List_WithTiedSortKey_WalksEveryPageWithoutDuplicatesOrGaps()
    {
        // Arrange: five rows with one Status and one UpdatedAt, as after a baseline import. Without a
        // unique final key, PostgreSQL may repeat or drop a row across pages.
        await SeedAsync(Row(5, "e"), Row(3, "c"), Row(1, "a"), Row(4, "d"), Row(2, "b"));

        // Act
        List<long> walked = [];
        PaginationResponse<TranslationListItemResponse> pageResult;
        int page = 0;
        do
        {
            pageResult = await ListAsync($"?sort=status&page={++page}&pageSize=2");
            walked.AddRange(pageResult.Items.Select(item => item.GossipId));
        }
        while (pageResult.HasNextPage);

        // Assert
        walked.ShouldBe([1L, 2L, 3L, 4L, 5L]);
    }

    [Fact]
    public async Task List_WithTiedSortKey_EndsWithTheUniqueTiebreaker()
    {
        // Arrange: one UpdatedAt for every row, seeded out of key order.
        await SeedAsync(
            Row(1, "x", fileId: 200),
            Row(2, "y", fileId: 100),
            Row(1, "z", fileId: 100));

        // Act
        PaginationResponse<TranslationListItemResponse> resultPage = await ListAsync("?sort=submittedAt");

        // Assert
        resultPage.Items.Select(item => (item.FileId, item.GossipId))
            .ShouldBe([(100, 1L), (100, 2L), (200, 1L)]);
    }

    [Theory]
    [InlineData("pl")]
    [InlineData("de")]
    public async Task List_WithALangOption_IgnoresIt(string lang)
    {
        // Arrange: the list holds only Polish rows and takes no language (#1478), so a lang option is
        // ignored like any unknown name.
        await SeedAsync(Row(1, "Frodo Baggins"), Row(2, "Samwise Gamgee"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync($"?lang={lang}");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L]);
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("draft")]
    public async Task List_WithUnknownStatus_ShouldReturn400(string status)
    {
        // Act: the status binds by its exact enum name, so a wrong case is unknown too.
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations?status={status}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task List_WithWholeWord_FindsTheTermOnlyAsAWordOfItsOwn()
    {
        // Arrange
        await SeedAsync(
            Row(1, "a ring of gold"),
            Row(2, "Ring[E]"),
            Row(3, "RING"),
            Row(4, "bring"),
            Row(5, "during"),
            Row(6, "Rings"),
            Row(7, "ring_x"),
            Row(8, "line one\nring"),
            Row(9, "ring2"),
            Row(10, "2ring"),
            Row(11, "x_ring"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync("ring", TranslationSearchMatch.WholeWord);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L, 3L, 8L]);
    }

    [Fact]
    public async Task List_WithWholeWord_TreatsPolishLettersAsLetters()
    {
        // Arrange: this also proves the test database's locale counts Polish letters as letters.
        await SeedAsync(Row(1, "Długi łuk"), Row(2, "łuku"), Row(3, "Półłuk"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync("łuk", TranslationSearchMatch.WholeWord);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Fact]
    public async Task List_WithWholeWordThatEndsInPunctuation_NeedsNoLetterRightBeforeOrAfterTheMatch()
    {
        // Arrange: the ticket's rule looks at the characters next to the match, so "Wait..." is no match.
        await SeedAsync(Row(1, "Hello ... there"), Row(2, "... and"), Row(3, "..."), Row(4, "Wait..."), Row(5, "...more"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync("...", TranslationSearchMatch.WholeWord);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public async Task List_WithWholeWordOfSeveralWords_MatchesThePhrase()
    {
        // Arrange
        await SeedAsync(Row(1, "Reduce Scale now"), Row(2, "Reduce the Scale"), Row(3, "Reduce Scales"));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await SearchAsync("reduce scale", TranslationSearchMatch.WholeWord);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Fact]
    public async Task List_WithWholeText_IgnoresEdgeWhiteSpaceAndOneGrammarMark()
    {
        // Arrange
        await SeedAsync(
            Row(1, "Ring"),
            Row(2, " Ring "),
            Row(3, "ring"),
            Row(4, "Ring[E]"),
            Row(5, "Ring [mn] "),
            Row(6, "Ring of Fire"),
            Row(7, "Ring[abcde]"),
            Row(8, "Ring[E][E]"),
            Row(9, "Ring\nother"),
            Row(10, "The Ring"),
            Row(11, "Ring[abcd]"),
            Row(12, "Ring[1]"),
            Row(13, "Ring[é]"),
            Row(14, "Ring[]"),
            Row(15, "\tRing\r\n"),
            Row(16, "other\nRing"),
            Row(17, "Ring[E]\n"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync("ring", TranslationSearchMatch.WholeText);

        // Assert: a mark is 1 to 4 ASCII letters, nothing else, and tabs and line breaks at the edges count as white space.
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L, 3L, 4L, 5L, 11L, 15L, 17L]);
    }

    [Fact]
    public async Task List_WithWholeTextTypedWithItsMark_FindsOnlyTheMarkedText()
    {
        // Arrange
        await SeedAsync(Row(1, "Ring[E]"), Row(2, "Ring"), Row(3, "Ring[F]"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync("Ring[E]", TranslationSearchMatch.WholeText);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Fact]
    public async Task List_WithWholeText_MatchesThePolishTextToo()
    {
        // Arrange
        await SeedAsync(
            Translated(1, "One Ring", "Pierścień"),
            Translated(2, "Ring of power", "Pierścień mocy"),
            Row(3, "Pierścień nie"));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await SearchAsync("PIERŚCIEŃ", TranslationSearchMatch.WholeText);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord)]
    [InlineData(TranslationSearchMatch.WholeText)]
    public async Task List_WithANewMode_IgnoresTheCaseOfPolishLetters(TranslationSearchMatch match)
    {
        // Arrange
        await SeedAsync(Row(1, "żółw"), Row(2, "żółwie"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync("ŻÓŁW", match);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord)]
    [InlineData(TranslationSearchMatch.WholeText)]
    public async Task List_WithAPaddedTermInANewMode_TrimsTheTermFirst(TranslationSearchMatch match)
    {
        // Arrange
        await SeedAsync(Row(1, "Ring[E]"), Row(2, "bring"));

        // Act: written by hand, so the test does not depend on what the shared request does with spaces.
        PaginationResponse<TranslationListItemResponse> page = await ListAsync($"?search=%20ring%20&match={match}");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord)]
    [InlineData(TranslationSearchMatch.WholeText)]
    public async Task List_WithABlankTermInANewMode_SearchesNothing(TranslationSearchMatch match)
    {
        // Arrange
        await SeedAsync(Row(1, "Ring"), Row(2, "Sword"));

        // Act: the shared request leaves a blank term out, so the address is written by hand.
        PaginationResponse<TranslationListItemResponse> page = await ListAsync($"?search=%20%20&match={match}");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L]);
    }

    [Theory]
    [MemberData(nameof(SpecialCharacterCases))]
    public async Task List_WithASpecialCharacterInANewMode_MatchesItLiterally(TranslationSearchMatch match, string character)
    {
        // Arrange: unescaped, most of these would match "abc" or "ac", or break the pattern.
        await SeedAsync(Row(1, $"a{character}c"), Row(2, "abc"), Row(3, "ac"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync($"a{character}c", match);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord, "🎁")]
    [InlineData(TranslationSearchMatch.WholeText, "🎁")]
    [InlineData(TranslationSearchMatch.WholeWord, "Ⅻ")]
    [InlineData(TranslationSearchMatch.WholeText, "Ⅻ")]
    [InlineData(TranslationSearchMatch.WholeWord, "„Ⅻ”")]
    [InlineData(TranslationSearchMatch.WholeText, "„Ⅻ”")]
    public async Task List_WithANonAsciiSymbolInANewMode_MatchesItLiterally(TranslationSearchMatch match, string term)
    {
        // Arrange: a backslash inside an emoji would split it, and Npgsql would refuse the text.
        await SeedAsync(Row(1, term), Row(2, "x"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync(term, match);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord)]
    [InlineData(TranslationSearchMatch.WholeText)]
    public async Task List_WithANewModeAndAStatus_AppliesBoth(TranslationSearchMatch match)
    {
        // Arrange
        await SeedAsync(
            Row(1, "Ring", TranslationStatus.Draft),
            Row(2, "Ring"),
            Row(3, "Rings", TranslationStatus.Draft));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync($"?search=ring&match={match}&status=Draft");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Fact]
    public async Task List_WithANewModeAndConflictsOnly_ListsOnlyTheMatchingConflictGroup()
    {
        // Arrange
        await SeedAsync(
            Translated(1, "Ring", "Pierścień"),
            Translated(2, "Ring", "Obrączka"),
            Translated(3, "Ring of Fire", "Krąg ognia"),
            Translated(4, "Ring of Fire", "Pierścień ognia"));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync("?search=ring&match=WholeText&conflictsOnly=true");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord)]
    [InlineData(TranslationSearchMatch.WholeText)]
    public async Task List_WithANumberInANewMode_ListsTheIdMatchAndTheModesTextMatch(TranslationSearchMatch match)
    {
        // Arrange: "1000" contains the term, so only the contains mode lists it.
        await SeedAsync(Row(100, "Kill wolves"), Row(200, "100"), Row(300, "1000"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync("100", match);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([100L, 200L]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord)]
    [InlineData(TranslationSearchMatch.WholeText)]
    public async Task List_WithAFileIdInANewMode_ListsEveryLiveRowOfThatFile(TranslationSearchMatch match)
    {
        // Arrange
        await SeedAsync(
            Row(1, "Frodo"),
            Row(2, "Samwise"),
            Row(1, "Gandalf", fileId: OtherFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await SearchAsync(FileId.ToString(CultureInfo.InvariantCulture), match);

        // Assert
        page.Items.Select(item => (item.FileId, item.GossipId)).ShouldBe([(FileId, 1L), (FileId, 2L)]);
    }

    [Theory]
    [InlineData(TranslationSearchMatch.WholeWord)]
    [InlineData(TranslationSearchMatch.WholeText)]
    public async Task List_WithFileIdColonGossipIdInANewMode_ListsThatRow(TranslationSearchMatch match)
    {
        // Arrange
        await SeedAsync(
            Row(6396484, "Frodo"),
            Row(6396485, "Samwise"),
            Row(6396484, "Gandalf", fileId: OtherFileId));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await SearchAsync($"{FileId}:6396484", match);

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        (item.FileId, item.GossipId).ShouldBe((FileId, 6396484L));
    }

    [Theory]
    [InlineData("Contains")]
    [InlineData("")]
    public async Task List_WithContainsOrNoMatch_FindsTheTermAnywhere(string matchOption)
    {
        // Arrange
        await SeedAsync(Row(1, "bring"), Row(2, "Ring"), Row(3, "Sword"));
        string query = matchOption.Length == 0 ? "?search=ring" : $"?search=ring&match={matchOption}";

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync(query);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L]);
    }

    [Theory]
    [InlineData("match=foo")]
    [InlineData("match=wholeword")]
    [InlineData("match=")]
    public async Task List_WithAMatchThatDoesNotBind_ShouldReturn400(string option)
    {
        // Act: like the status, the mode binds by its exact enum name.
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations?search=ring&{option}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
    }

    [Theory]
    [InlineData("match=7")]
    [InlineData("match=-1")]
    [InlineData("match=WholeWord,WholeText")]
    public async Task List_WithAMatchThatIsNoMode_ShouldReturn400WithItsCode(string option)
    {
        // Act: binding accepts a number and a comma list, so the handler refuses the undefined value.
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations?search=ring&{option}");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        problem.RootElement.GetProperty("errorCode").GetString().ShouldBe("Translations.UnknownSearchMatch");
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public async Task List_WithAnAddressBuiltFromTheSharedRequest_AppliesEveryOption(
        ListTranslationsRequest request,
        long[] expectedGossipIds,
        int expectedTotal)
    {
        // Arrange: each option changes the result on its own. The address comes from the record the
        // website uses, so a name the API stops binding fails here (#1478).
        await SeedAsync(
            Translated(1, "Stout-axe[D]", "Topór wojenny"),
            Translated(2, "Stout-axe[D]", "Krzepki Topornik"),
            Translated(3, "Stout-axe[D]", "Wytrzymały topór"),
            Translated(4, "Stout-axe[D]", "Topór", approve: true),
            Translated(5, "Dwarf[D]", "Krasnolud"),
            Translated(6, "Dwarf[D]", "Krasnoludek"),
            Translated(7, "Stout heart", "Dzielne serce"),
            Row(8, "Stout-axe[D]"),
            Translated(9, "Stoutly[D]", "Dzielnie"),
            Translated(10, "Stoutly[D]", "Mężnie"));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsync(QueryHelpers.AddQueryString(string.Empty, request.ToQueryParameters()));

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe(expectedGossipIds);
        page.TotalCount.ShouldBe(expectedTotal);
    }

    [Fact]
    public async Task List_WithoutToken_ShouldServeReadOnlyRows()
    {
        // Arrange: the list is public (#309).
        await SeedAsync(Row(1, "Frodo Baggins", TranslationStatus.Draft));

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/v1/translations");
        PaginationResponse<TranslationListItemResponse> page =
            (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        item.SourceText.ShouldBe("Frodo Baggins");
        item.TranslatedText.ShouldBe("Polski tekst");
    }

    [Fact]
    public async Task List_WithExpiredToken_ShouldServeTheAnonymousReadOnlyView()
    {
        // Arrange: on an anonymous endpoint a rejected token is no 401, just an anonymous caller. The
        // dead-session check fires on pages that need a login.
        await SeedAsync(Row(1, "Frodo Baggins", TranslationStatus.Draft));
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateExpiredAccessToken());

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/translations");
        PaginationResponse<TranslationListItemResponse> page =
            (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        item.Links.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_WithoutToken_ShouldSupportSearchAndStatusFilter()
    {
        // Arrange: the public view keeps search and filters.
        await SeedAsync(
            Row(1, "Frodo Baggins", TranslationStatus.Draft),
            Row(2, "Samwise Gamgee"));

        // Act
        HttpResponseMessage response = await _factory.CreateClient()
            .GetAsync("/api/v1/translations?search=Frodo&status=Draft");
        PaginationResponse<TranslationListItemResponse> page =
            (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Items.ShouldHaveSingleItem().GossipId.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithConflictsOnly_ShouldListTheTranslatedRowsOfEveryGroupWithDifferentPolish()
    {
        // Arrange: the example of #1347, an agreeing group, an untranslated twin and a unique row.
        await SeedAsync(
            Translated(49821557, "Stout-axe[D]", "Topór wojenny", approve: true),
            Translated(127768437, "Stout-axe[D]", "Krzepki Topornik"),
            Translated(204170133, "Stout-axe[D]", "Wytrzymały topór"),
            Row(300000000, "Stout-axe[D]"),
            Translated(1, "Dwarf[D]", "Krasnolud"),
            Translated(2, "Dwarf[D]", "Krasnolud", approve: true),
            Translated(3, "High Elf[L]", "Wysoki elf"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true");

        // Assert
        page.TotalCount.ShouldBe(3);
        page.Items.Select(item => item.GossipId).ShouldBe([49821557L, 127768437L, 204170133L]);
    }

    [Fact]
    public async Task List_WithConflictsOnly_ShouldNotCountAnUntranslatedOrARemovedTwinAsADifference()
    {
        // Arrange
        await SeedAsync(
            Translated(1, "Dwarf[D]", "Krasnolud"),
            Row(2, "Dwarf[D]"),
            Translated(3, "Beorning[B]", "Beorning"),
            Translated(4, "Beorning[B]", "Niedźwiedziołak", removed: true));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true");

        // Assert
        page.TotalCount.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_WithConflictsOnlyAndStatus_ShouldJudgeTheConflictOnTheWholeGroup()
    {
        // Arrange: the status narrows the listed rows, never the rows the conflict is judged on.
        await SeedAsync(
            Translated(1, "Stout-axe[D]", "Topór wojenny", approve: true),
            Translated(2, "Stout-axe[D]", "Krzepki Topornik"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true&status=Draft");

        // Assert
        TranslationListItemResponse item = page.Items.ShouldHaveSingleItem();
        item.GossipId.ShouldBe(2);
        item.Status.ShouldBe(TranslationStatus.Draft);
    }

    [Fact]
    public async Task List_WithConflictsOnlyAndSearch_ShouldListOnlyTheMatchingGroup()
    {
        // Arrange
        await SeedAsync(
            Translated(1, "Stout-axe[D]", "Topór wojenny"),
            Translated(2, "Stout-axe[D]", "Krzepki Topornik"),
            Translated(3, "Dwarf[D]", "Krasnolud"),
            Translated(4, "Dwarf[D]", "Krasnoludek"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true&search=Stout");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task List_WithConflictsOnly_ShouldPage()
    {
        // Arrange
        await SeedAsync(
            Translated(1, "Stout-axe[D]", "Topór wojenny"),
            Translated(2, "Stout-axe[D]", "Krzepki Topornik"),
            Translated(3, "Stout-axe[D]", "Wytrzymały topór"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true&page=2&pageSize=2");

        // Assert
        page.TotalCount.ShouldBe(3);
        page.Items.ShouldHaveSingleItem().GossipId.ShouldBe(3);
    }

    [Fact]
    public async Task List_WithConflictsOnlyAndSort_ShouldSortTheConflictRows()
    {
        // Arrange
        await SeedAsync(
            Translated(1, "Stout-axe[D]", "Topór wojenny"),
            Translated(2, "Stout-axe[D]", "Krzepki Topornik"),
            Translated(3, "Stout-axe[D]", "Wytrzymały topór"),
            Translated(4, "Dwarf[D]", "Krasnolud"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true&sort=gossipId:desc");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([3L, 2L, 1L]);
    }

    [Theory]
    [InlineData("Topór wojenny", "topór wojenny")]
    [InlineData("Topór wojenny", "Topór wojenny ")]
    [InlineData("Topór wojenny", "Topór  wojenny")]
    public async Task List_WithConflictsOnly_ShouldTreatPolishThatDiffersOnlyInCaseOrSpacesAsAConflict(
        string first,
        string second)
    {
        // Arrange: Polish is compared exactly, as the game file would differ too.
        await SeedAsync(Translated(1, "Stout-axe[D]", first), Translated(2, "Stout-axe[D]", second));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true");

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task List_WithConflictsOnly_ShouldNotGroupEnglishThatDiffersOnlyInCase()
    {
        // Arrange
        await SeedAsync(Translated(1, "Stout-axe[D]", "Topór wojenny"), Translated(2, "stout-axe[D]", "Krzepki Topornik"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsync("?conflictsOnly=true");

        // Assert
        page.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_WithoutToken_ShouldSupportTheConflictsOnlyFilter()
    {
        // Arrange: the public list keeps every filter (#309).
        await SeedAsync(
            Translated(1, "Stout-axe[D]", "Topór wojenny"),
            Translated(2, "Stout-axe[D]", "Krzepki Topornik"),
            Translated(3, "Dwarf[D]", "Krasnolud"));

        // Act
        HttpResponseMessage response = await _factory.CreateClient()
            .GetAsync("/api/v1/translations?conflictsOnly=true");
        PaginationResponse<TranslationListItemResponse> page =
            (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task List_WithMine_ListsOnlyTheLiveRowsWhoseCurrentTextTheCallerSaved()
    {
        // Arrange: the caller comes from the token, never from the address (#1491).
        Guid callerSubject = Guid.NewGuid();
        TranslatorId callerId = await SeedTranslatorAsync(callerSubject, "Anna");
        await SeedAsync(
            Saved(1, "Decayed", "Zgniły", callerId, Now.AddMinutes(1)),
            Saved(2, "Rotten", "Zepsuty", _seederId, Now.AddMinutes(2)),
            Row(3, "Untouched"),
            Saved(4, "Decayed bones", "Zgniłe kości", callerId, Now.AddMinutes(3), removed: true));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsAsync(
            callerSubject, AuthConstants.Roles.Translator, new ListTranslationsRequest(Mine: true));

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
        page.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task List_WithMine_DropsAnApprovedRowThatAnotherTranslatorSavedAgain()
    {
        // Arrange: "mine" is the last save of the row's text, so a later save by someone else takes the row,
        // an approved one included. Spec 0014 keeps the old author on an approved row once every save is a
        // proposal (#1384): this test then fails until the filter reads the newest proposal.
        Guid callerSubject = Guid.NewGuid();
        Guid otherSubject = Guid.NewGuid();
        TranslatorId callerId = await SeedTranslatorAsync(callerSubject, "Anna");
        await SeedAsync(
            Saved(1, "Decayed", "Zgniły", callerId, Now, approvedBy: _seederId),
            Saved(2, "Rotten", "Zepsuty", callerId, Now));

        // Act
        HttpResponseMessage save = await ClientFor(otherSubject, AuthConstants.Roles.Translator)
            .PutAsJsonAsync("/api/v1/translations", new UpsertTranslationRequest(FileId, 1, "Rozłożony"));
        PaginationResponse<TranslationListItemResponse> callerPage = await ListAsAsync(
            callerSubject, AuthConstants.Roles.Translator, new ListTranslationsRequest(Mine: true));
        PaginationResponse<TranslationListItemResponse> otherPage = await ListAsAsync(
            otherSubject, AuthConstants.Roles.Translator, new ListTranslationsRequest(Mine: true));

        // Assert
        save.StatusCode.ShouldBe(HttpStatusCode.OK);
        callerPage.Items.Select(item => item.GossipId).ShouldBe([2L]);
        otherPage.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Fact]
    public async Task List_WithMine_ForAnAdmin_ListsTheAdminsOwnSaves()
    {
        // Arrange: the translator profile decides, not a role; this token holds only Admin.
        Guid adminSubject = Guid.NewGuid();
        TranslatorId adminId = await SeedTranslatorAsync(adminSubject, "Admin");
        await SeedAsync(
            Saved(1, "Decayed", "Zgniły", adminId, Now),
            Saved(2, "Rotten", "Zepsuty", _seederId, Now));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsAsync(
            adminSubject, AuthConstants.Roles.Admin, new ListTranslationsRequest(Mine: true));

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
    }

    [Fact]
    public async Task List_WithMineAndNoSort_ShowsTheNewestChangeFirstThenFileIdThenGossipId()
    {
        // Arrange: three rows share one time, as the rows of one bulk approve do.
        Guid callerSubject = Guid.NewGuid();
        TranslatorId callerId = await SeedTranslatorAsync(callerSubject, "Anna");
        await SeedAsync(
            Saved(1, "a", "a", callerId, Now.AddMinutes(1)),
            Saved(3, "b", "b", callerId, Now.AddMinutes(5), fileId: OtherFileId),
            Saved(2, "c", "c", callerId, Now.AddMinutes(5), fileId: OtherFileId),
            Saved(9, "d", "d", callerId, Now.AddMinutes(5)),
            Saved(4, "e", "e", callerId, Now.AddMinutes(9)));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsAsync(
            callerSubject, AuthConstants.Roles.Translator, new ListTranslationsRequest(Mine: true));

        // Assert
        page.Items.Select(item => (item.FileId, item.GossipId)).ShouldBe(
            [(FileId, 4L), (FileId, 9L), (OtherFileId, 2L), (OtherFileId, 3L), (FileId, 1L)]);
    }

    [Fact]
    public async Task List_WithMineAndASort_UsesTheSort()
    {
        // Arrange: the newest change is the last row, so the default order would put it first.
        Guid callerSubject = Guid.NewGuid();
        TranslatorId callerId = await SeedTranslatorAsync(callerSubject, "Anna");
        await SeedAsync(
            Saved(1, "a", "a", callerId, Now.AddMinutes(1)),
            Saved(2, "b", "b", callerId, Now.AddMinutes(5)),
            Saved(3, "c", "c", callerId, Now.AddMinutes(9)));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsAsync(
            callerSubject, AuthConstants.Roles.Translator, new ListTranslationsRequest(Mine: true, Sort: "gossipId:asc"));

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L, 2L, 3L]);
    }

    [Theory]
    [MemberData(nameof(MineWithOtherFilterCases))]
    public async Task List_WithMineAndOtherFilters_AppliesThemAll(ListTranslationsRequest request, long[] expectedGossipIds)
    {
        // Arrange: the caller saved rows 1, 3, 4 and 6. Row 2 gives row 1's English a different Polish; rows
        // 4 and 5 agree, so they are no conflict.
        Guid callerSubject = Guid.NewGuid();
        TranslatorId callerId = await SeedTranslatorAsync(callerSubject, "Anna");
        await SeedAsync(
            Saved(1, "Stout-axe[D]", "Topór wojenny", callerId, Now.AddMinutes(1)),
            Saved(2, "Stout-axe[D]", "Krzepki Topornik", _seederId, Now.AddMinutes(2)),
            Saved(3, "Dwarf[D]", "Krasnolud", callerId, Now.AddMinutes(3), approvedBy: _seederId),
            Saved(4, "Stout heart", "Dzielne serce", callerId, Now.AddMinutes(4)),
            Saved(5, "Stout heart", "Dzielne serce", _seederId, Now.AddMinutes(5)),
            Saved(6, "Stoutly[D]", "Dzielnie", callerId, Now.AddMinutes(6)));

        // Act
        PaginationResponse<TranslationListItemResponse> page =
            await ListAsAsync(callerSubject, AuthConstants.Roles.Translator, request);

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe(expectedGossipIds);
        page.TotalCount.ShouldBe(expectedGossipIds.Length);
    }

    [Fact]
    public async Task List_WithMine_Pages()
    {
        // Arrange
        Guid callerSubject = Guid.NewGuid();
        TranslatorId callerId = await SeedTranslatorAsync(callerSubject, "Anna");
        await SeedAsync(
            Saved(1, "a", "a", callerId, Now.AddMinutes(1)),
            Saved(2, "b", "b", callerId, Now.AddMinutes(2)),
            Saved(3, "c", "c", callerId, Now.AddMinutes(3)),
            Saved(4, "d", "d", _seederId, Now.AddMinutes(4)));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsAsync(
            callerSubject, AuthConstants.Roles.Translator, new ListTranslationsRequest(Mine: true, Page: 2, PageSize: 2));

        // Assert
        page.Items.Select(item => item.GossipId).ShouldBe([1L]);
        page.TotalCount.ShouldBe(3);
        page.HasPreviousPage.ShouldBeTrue();
        page.HasNextPage.ShouldBeFalse();
    }

    [Fact]
    public async Task List_WithMineAndNoToken_ReturnsAnEmptyPage()
    {
        // Arrange: an anonymous caller saved nothing, and the public list never answers 401 (#1491). The
        // untranslated row has no author, so a filter on a missing caller would list it.
        await SeedAsync(Translated(1, "Decayed", "Zgniły"), Row(2, "Untouched"));

        // Act
        HttpResponseMessage response = await _factory.CreateClient().GetAsync("/api/v1/translations?mine=true");
        PaginationResponse<TranslationListItemResponse> page =
            (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task List_WithMineAndAnExpiredToken_ReturnsAnEmptyPageNotTheTokensRows()
    {
        // Arrange: the refused token names a caller who saved a row; a refused token is no caller at all.
        // The untranslated row has no author, so a filter on a missing caller would list it.
        Guid callerSubject = Guid.NewGuid();
        TranslatorId callerId = await SeedTranslatorAsync(callerSubject, "Anna");
        await SeedAsync(Saved(1, "Decayed", "Zgniły", callerId, Now), Row(2, "Untouched"));
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateExpiredAccessToken(callerSubject));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/translations?mine=true");
        PaginationResponse<TranslationListItemResponse> page =
            (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task List_WithMineForACallerWithNoTranslatorProfile_ReturnsAnEmptyPage()
    {
        // Arrange: a token with no usable name gets no profile (INV-4.4), and with no profile there is no
        // save to list. The untranslated row has no author, so a filter on a missing profile would list it.
        await SeedAsync(Translated(1, "Decayed", "Zgniły"), Row(2, "Untouched"));
        Guid subject = Guid.NewGuid();
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            TranslationSystemApiFactory.CreateAccessToken(
                AuthConstants.Roles.Translator, AuthConstants.Scopes.Api, subject, displayName: "", email: ""));

        // Act
        HttpResponseMessage response = await client.GetAsync("/api/v1/translations?mine=true");
        PaginationResponse<TranslationListItemResponse> page =
            (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(0);
        (await TranslatorExistsAsync(subject)).ShouldBeFalse();
    }

    [Fact]
    public async Task List_WithMineForACallerWhoSavedNothing_ReturnsAnEmptyPage()
    {
        // Arrange: the first request makes this caller's profile (INV-4.4), and it holds no save.
        await SeedAsync(Translated(1, "Decayed", "Zgniły"));

        // Act
        PaginationResponse<TranslationListItemResponse> page = await ListAsAsync(
            Guid.NewGuid(), AuthConstants.Roles.Translator, new ListTranslationsRequest(Mine: true));

        // Assert
        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(0);
    }

    public static TheoryData<ListTranslationsRequest, long[]> MineWithOtherFilterCases() => new()
    {
        { new ListTranslationsRequest(Mine: true), [6, 4, 3, 1] },
        { new ListTranslationsRequest(Mine: true, Search: "Stout"), [6, 4, 1] },
        { new ListTranslationsRequest(Mine: true, Search: "Stout", Match: TranslationSearchMatch.WholeWord), [4, 1] },
        { new ListTranslationsRequest(Mine: true, Search: "Stout heart", Match: TranslationSearchMatch.WholeText), [4] },
        { new ListTranslationsRequest(Mine: true, Status: TranslationStatus.Approved), [3] },
        { new ListTranslationsRequest(Mine: true, ConflictsOnly: true), [1] },
        {
            new ListTranslationsRequest(
                Mine: true,
                Search: "Stout",
                Status: TranslationStatus.Draft,
                ConflictsOnly: true,
                Match: TranslationSearchMatch.WholeWord),
            [1]
        }
    };

    public static TheoryData<ListTranslationsRequest, long[], int> RoundTripCases() => new()
    {
        { new ListTranslationsRequest(), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 10 },
        { new ListTranslationsRequest(Search: "Stout"), [1, 2, 3, 4, 7, 8, 9, 10], 8 },
        { new ListTranslationsRequest(Search: "Stout", Match: TranslationSearchMatch.WholeWord), [1, 2, 3, 4, 7, 8], 6 },
        { new ListTranslationsRequest(Status: TranslationStatus.Draft), [1, 2, 3, 5, 6, 7, 9, 10], 8 },
        { new ListTranslationsRequest(Page: 2), [], 10 },
        { new ListTranslationsRequest(PageSize: 3), [1, 2, 3], 10 },
        { new ListTranslationsRequest(Sort: "gossipId:desc"), [10, 9, 8, 7, 6, 5, 4, 3, 2, 1], 10 },
        { new ListTranslationsRequest(ConflictsOnly: true), [1, 2, 3, 4, 5, 6, 9, 10], 8 },
        // This caller saved nothing, so "mine" holds nothing.
        { new ListTranslationsRequest(Mine: true), [], 0 },
        {
            new ListTranslationsRequest(
                Search: "Stout",
                Status: TranslationStatus.Draft,
                Page: 2,
                PageSize: 2,
                Sort: "gossipId:desc",
                ConflictsOnly: true,
                Match: TranslationSearchMatch.WholeWord),
            [1],
            3
        }
    };

    /// <summary>
    /// A sign, a space, a separator, a number too big for a long, or a bad colon form. Each decoy holds
    /// the ids a lenient parse would read: it drops the sign or the separator, or clamps an overflow to
    /// the column's maximum. A parse that keeps the minus or wraps to a negative number has no row to
    /// hit, because ids are never negative.
    /// </summary>
    public static TheoryData<string, int, long> TextOnlyTerms() => new()
    {
        { "+5", 5, 5 },
        { "-5", 5, 5 },
        { "1 5", 15, 15 },
        { "1,000", 1000, 1000 },
        { "5.0", 5, 5 },
        { "1.5", 15, 15 },
        { "9223372036854775808", int.MaxValue, long.MaxValue },
        { "2147483648:5", int.MaxValue, 5 },
        { "5:9223372036854775808", 5, long.MaxValue },
        { "5: 6", 5, 6 },
        { "5 :6", 5, 6 },
        { "5:+6", 5, 6 },
        { "5::6", 5, 6 },
        { "5:6:7", 5, 6 },
        { "5:", 5, 5 },
        { ":5", 5, 5 }
    };

    public static TheoryData<TranslationSearchMatch, string> SpecialCharacterCases()
    {
        TheoryData<TranslationSearchMatch, string> cases = new();
        string[] characters = [".", "*", "+", "?", "(", ")", "[", "]", "{", "}", "|", "^", "$", "\\", "%", "_", "-", "#", "\t"];
        foreach (TranslationSearchMatch match in new[] { TranslationSearchMatch.WholeWord, TranslationSearchMatch.WholeText })
        {
            foreach (string character in characters)
            {
                cases.Add(match, character);
            }
        }

        return cases;
    }

    private async Task<PaginationResponse<TranslationListItemResponse>> ListAsync(string queryString)
    {
        HttpResponseMessage response = await TranslatorClient().GetAsync($"/api/v1/translations{queryString}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;
    }

    /// <summary>
    /// Built from the record the website uses, so a <c>+</c> or a space in the search reaches the API as
    /// typed and is not read as a space.
    /// </summary>
    private Task<PaginationResponse<TranslationListItemResponse>> ListAsync(ListTranslationsRequest request) =>
        ListAsync(QueryHelpers.AddQueryString(string.Empty, request.ToQueryParameters()));

    private Task<PaginationResponse<TranslationListItemResponse>> SearchAsync(string term, TranslationSearchMatch match)
        => ListAsync(new ListTranslationsRequest(Search: term, Match: match));

    private async Task<PaginationResponse<TranslationListItemResponse>> ListAsAsync(
        Guid subject,
        string role,
        ListTranslationsRequest request)
    {
        HttpResponseMessage response = await ClientFor(subject, role).GetAsync(
            QueryHelpers.AddQueryString("/api/v1/translations", request.ToQueryParameters()));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaginationResponse<TranslationListItemResponse>>(JsonOptions))!;
    }

    private HttpClient ClientFor(Guid subject, string role)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(role, AuthConstants.Scopes.Api, subject));
        return client;
    }

    private HttpClient TranslatorClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", TranslationSystemApiFactory.CreateAccessToken(AuthConstants.Roles.Translator));
        return client;
    }

    private async Task SeedAsync(params Translation[] rows)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        dbContext.Translations.AddRange(rows);
        await dbContext.SaveChangesAsync();
    }

    private async Task<bool> TranslatorExistsAsync(Guid subject)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        IdentityId identityId = IdentityId.Create(subject);
        return await dbContext.Translators.AnyAsync(translator => translator.IdentityId == identityId);
    }

    private async Task<TranslatorId> SeedTranslatorAsync(Guid subject, string displayName)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        ApplicationWriteDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationWriteDbContext>();
        Translator translator = Translator.Create(
            IdentityId.Create(subject), DisplayName.Create(displayName).Value, email: null, Now).Value;
        dbContext.Translators.Add(translator);
        await dbContext.SaveChangesAsync();
        return translator.Id;
    }

    /// <summary>
    /// A row whose text <paramref name="author"/> saved at <paramref name="at"/>. An approve happens at the
    /// same time, so the row's last change stays <paramref name="at"/>.
    /// </summary>
    private Translation Saved(
        long gossipId,
        string source,
        string polish,
        TranslatorId author,
        DateTimeOffset at,
        TranslatorId? approvedBy = null,
        bool removed = false,
        int fileId = FileId)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(fileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation(polish, author, at);

        if (approvedBy is { } approver)
        {
            row.Approve(approver, at);
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, at);
        }

        return row;
    }

    private Translation Row(
        long gossipId,
        string source,
        TranslationStatus status = TranslationStatus.Untranslated,
        bool removed = false,
        int fileId = FileId)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(fileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;

        switch (status)
        {
            case TranslationStatus.Draft:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                break;
            case TranslationStatus.NeedsReview:
                row.ProvideTranslation("Polski tekst", _seederId, Now);
                row.ApplySourceChange(TranslationSource.Create($"{source} (reworded)", null, null).Value, _versionId, Now);
                break;
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, Now);
        }

        return row;
    }

    private Translation Translated(
        long gossipId,
        string source,
        string polish,
        bool approve = false,
        bool removed = false,
        int fileId = FileId)
    {
        Translation row = Translation.CreateUntranslated(
            FragmentKey.Create(fileId, gossipId).Value,
            TranslationSource.Create(source, null, null).Value,
            _versionId,
            Now).Value;
        row.ProvideTranslation(polish, _seederId, Now);

        if (approve)
        {
            row.Approve(_seederId, Now);
        }

        if (removed)
        {
            row.MarkRemoved(_versionId, Now);
        }

        return row;
    }
}
