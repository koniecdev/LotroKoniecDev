using System.Linq.Expressions;
using System.Text;
using LotroKoniecDev.TranslationSystem.API.Features.Translations;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.GameVersionAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate;
using LotroKoniecDev.TranslationSystem.Primitives.Aggregates.TranslationAggregate.Enums;
using LotroKoniecDev.TranslationSystem.ReadModels.Aggregates.TranslationAggregate;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Features.Translations;

/// <summary>
/// The sort, the place and the "after" filter must agree (ADR-0077). These tests walk an in-memory list;
/// the integration suite walks the same list in PostgreSQL.
/// </summary>
public sealed class TranslationListOrderTests
{
    private const int FileId = 620756992;
    private const int OtherFileId = 620756993;

    private static readonly DateTimeOffset Now = new(2026, 6, 13, 0, 0, 0, TimeSpan.Zero);
    private static readonly GameVersionId VersionId = GameVersionId.Create();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryReadPlace_ForEveryRowOfTheList_SelectsExactlyTheRowsAfterItInTheSameOrder(bool mine)
    {
        // Arrange: three rows share one change time, as the rows of one bulk approve do.
        TranslationListOrder order = TranslationListOrder.For(mine);
        List<TranslationReadModel> rows = order.Sort(ListWithTies().AsQueryable()).ToList();

        for (int index = 0; index < rows.Count; index++)
        {
            TranslationReadModel row = rows[index];

            // Act
            bool read = order.TryReadPlace(order.PlaceOf(row.FileId, row.GossipId, row.UpdatedAt), out Expression<Func<TranslationReadModel, bool>>? isAfter);

            // Assert
            read.ShouldBeTrue();
            order.Sort(rows.AsQueryable().Where(isAfter!)).ShouldBe(rows.Skip(index + 1));
        }
    }

    [Fact]
    public void TryReadPlace_WithTheNewestChangeFirst_KeepsTheMicrosecondsOfTheChangeTime()
    {
        // Arrange: PostgreSQL keeps microseconds, so two saves can differ by one.
        DateTimeOffset changedAt = Now.AddTicks(123_450);
        TranslationReadModel sameTime = Row(FileId, 1, changedAt);
        TranslationReadModel oneMicrosecondEarlier = Row(FileId, 1, changedAt.AddTicks(-10));
        string place = TranslationListOrder.NewestChangeFirst.PlaceOf(FileId, 1, changedAt);

        // Act
        TranslationListOrder.NewestChangeFirst.TryReadPlace(place, out Expression<Func<TranslationReadModel, bool>>? isAfter)
            .ShouldBeTrue();
        Func<TranslationReadModel, bool> isAfterPlace = isAfter!.Compile();

        // Assert
        isAfterPlace(sameTime).ShouldBeFalse();
        isAfterPlace(oneMicrosecondEarlier).ShouldBeTrue();
    }

    [Fact]
    public void TryReadPlace_WithTheNewestChangeFirst_PutsALaterSaveOfTheSameRowBeforeTheOldPlace()
    {
        // Arrange: a save sets the change time to now, so the row moves up, never after its old place.
        string oldPlace = TranslationListOrder.NewestChangeFirst.PlaceOf(FileId, 1, Now);
        TranslationReadModel savedAgain = Row(FileId, 1, Now.AddSeconds(1));

        // Act
        TranslationListOrder.NewestChangeFirst.TryReadPlace(oldPlace, out Expression<Func<TranslationReadModel, bool>>? isAfter)
            .ShouldBeTrue();

        // Assert
        isAfter!.Compile()(savedAgain).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(UnreadablePlaces))]
    public void TryReadPlace_WithAPlaceThisOrderDidNotWrite_RefusesWithoutThrowing(bool mine, string place)
    {
        // Act
        bool read = TranslationListOrder.For(mine).TryReadPlace(place, out Expression<Func<TranslationReadModel, bool>>? isAfter);

        // Assert
        read.ShouldBeFalse();
        isAfter.ShouldBeNull();
    }

    [Fact]
    public void TryReadPlace_WithAPlaceOfTheOtherOrder_Refuses()
    {
        // Arrange
        string keyPlace = TranslationListOrder.ByKey.PlaceOf(FileId, 1, Now);
        string minePlace = TranslationListOrder.NewestChangeFirst.PlaceOf(FileId, 1, Now);

        // Act & Assert
        TranslationListOrder.NewestChangeFirst.TryReadPlace(keyPlace, out _).ShouldBeFalse();
        TranslationListOrder.ByKey.TryReadPlace(minePlace, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlaceOf_WritesOnlyUrlSafeCharacters(bool mine)
    {
        // Act
        string place = TranslationListOrder.For(mine).PlaceOf(int.MinValue, long.MaxValue, DateTimeOffset.MaxValue);

        // Assert
        place.ShouldAllBe(character => char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_');
    }

    public static TheoryData<bool, string> UnreadablePlaces()
    {
        TheoryData<bool, string> cases = new()
        {
            { false, "" },
            { false, "!!!not base64!!!" },
            { false, Encode("k:620756992") },
            { false, Encode("k:620756992:1:2") },
            { false, Encode("k:x:1") },
            { false, Encode("k:620756992:y") },
            { false, Encode("k:99999999999:1") },
            { false, Encode("K:620756992:1") },
            { false, new string('a', 200) },
            { true, Encode("m:1:620756992") },
            { true, Encode("m:-1:620756992:1") },
            { true, Encode($"m:{DateTimeOffset.MaxValue.UtcTicks + 1}:620756992:1") },
            { true, Encode("m:99999999999999999999999:620756992:1") },
            { true, Encode("m: 1:620756992:1") },
            { true, Encode("m:1:620756992:1:9") }
        };

        return cases;
    }

    private static string Encode(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IEnumerable<TranslationReadModel> ListWithTies() =>
    [
        Row(OtherFileId, 3, Now.AddMinutes(5)),
        Row(FileId, 1, Now.AddMinutes(1)),
        Row(FileId, 9, Now.AddMinutes(5)),
        Row(OtherFileId, 2, Now.AddMinutes(5)),
        Row(FileId, 4, Now.AddMinutes(9)),
        Row(FileId, 2, Now)
    ];

    private static TranslationReadModel Row(int fileId, long gossipId, DateTimeOffset updatedAt) =>
        new(
            TranslationId.Create(),
            fileId,
            gossipId,
            $"source-{gossipId}",
            null,
            null,
            "Polski tekst",
            null,
            null,
            null,
            TranslationStatus.Draft,
            VersionId,
            null,
            null,
            Now,
            updatedAt);
}
