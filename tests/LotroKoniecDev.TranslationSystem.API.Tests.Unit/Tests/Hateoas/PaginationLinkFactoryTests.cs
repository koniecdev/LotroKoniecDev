using System.Globalization;
using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
using Microsoft.AspNetCore.Routing;
using NSubstitute;

namespace LotroKoniecDev.TranslationSystem.API.Tests.Unit.Tests.Hateoas;

public sealed class PaginationLinkFactoryTests
{
    private const string EndpointName = "ListTranslations";

    private readonly ILinkFactory _linkFactory = Substitute.For<ILinkFactory>();
    private readonly PaginationLinkFactory _sut;

    public PaginationLinkFactoryTests()
    {
        _linkFactory
            .CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object?>())
            .Returns(call => new LinkDto("/api/v1/translations", call.ArgAt<string>(1), call.ArgAt<string>(2)));

        _sut = new PaginationLinkFactory(_linkFactory);
    }

    [Theory]
    [InlineData(1, 3, 50, false, false, false, false)]
    [InlineData(1, 2, 1, false, true, false, true)]
    [InlineData(2, 2, 1, true, false, true, false)]
    [InlineData(2, 3, 1, true, true, true, true)]
    [InlineData(1, 0, 50, false, false, false, false)]
    [InlineData(99, 3, 1, true, true, true, false)]
    [InlineData(5, 0, 50, true, false, true, false)]
    public async Task CreatePaginationLinks_AcrossThePageBoundaries_AdvertisesARelOnlyWhenItLeadsElsewhere(
        int page,
        int totalCount,
        int pageSize,
        bool expectFirst,
        bool expectLast,
        bool expectPrevious,
        bool expectNext)
    {
        // Arrange
        PaginationResponse<string> response = new()
        {
            Items = [],
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };

        // Act
        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response, NoFilters);

        // Assert
        links.Any(l => l.Rel == Rels.Self).ShouldBeTrue();
        links.Any(l => l.Rel == Rels.FirstPage).ShouldBe(expectFirst);
        links.Any(l => l.Rel == Rels.LastPage).ShouldBe(expectLast);
        links.Any(l => l.Rel == Rels.PreviousPage).ShouldBe(expectPrevious);
        links.Any(l => l.Rel == Rels.NextPage).ShouldBe(expectNext);
    }

    [Fact]
    public async Task CreatePaginationLinks_OnASinglePage_AdvertisesNothingButSelf()
    {
        // Any boundary rel here would reload the page the user is on (#545).
        PaginationResponse<string> response = new()
        {
            Items = [],
            Page = 1,
            PageSize = 50,
            TotalCount = 3
        };

        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response, NoFilters);

        links.ShouldHaveSingleItem().Rel.ShouldBe(Rels.Self);
    }

    [Fact]
    public async Task CreatePaginationLinks_OnAnOverRangePage_KeepsBothAbsoluteJumpsSoTheCallerCanGetBack()
    {
        // Page is clamped at the lower bound only, so ?page=99 of 3 is reachable; first and last are
        // the way back.
        PaginationResponse<string> response = new()
        {
            Items = [],
            Page = 99,
            PageSize = 1,
            TotalCount = 3
        };

        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response, NoFilters);

        links.Select(l => l.Rel).ShouldBe([Rels.Self, Rels.FirstPage, Rels.LastPage, Rels.PreviousPage], ignoreOrder: true);
    }

    [Fact]
    public async Task CreatePaginationLinks_WhenTheLinkFactoryRefusesARel_OmitsItRatherThanEmittingNull()
    {
        // ILinkFactory returns null for a target the caller may not reach (ADR-0040).
        _linkFactory
            .CreateAsync(Arg.Any<string>(), Rels.LastPage, Arg.Any<string>(), Arg.Any<object?>())
            .Returns((LinkDto?)null);

        PaginationResponse<string> response = new()
        {
            Items = [],
            Page = 1,
            PageSize = 1,
            TotalCount = 2
        };

        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response, NoFilters);

        links.Any(l => l.Rel == Rels.LastPage).ShouldBeFalse();
        links.Any(l => l.Rel == Rels.NextPage).ShouldBeTrue();
    }

    [Fact]
    public async Task CreatePaginationLinks_BuildsEachRelFromTheQueryOfThePageItLeadsTo()
    {
        // Arrange: page 3 of 5 carries every rel. The stub echoes the query the factory handed over.
        _linkFactory
            .CreateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object?>())
            .Returns(call => new LinkDto(
                $"/list?at={((RouteValueDictionary)call.ArgAt<object?>(3)!)["at"]}",
                call.ArgAt<string>(1),
                call.ArgAt<string>(2)));

        PaginationResponse<string> response = new()
        {
            Items = [],
            Page = 3,
            PageSize = 1,
            TotalCount = 5
        };

        // Act
        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(
            EndpointName,
            response,
            page => [new KeyValuePair<string, string?>("at", page.ToString(CultureInfo.InvariantCulture))]);

        // Assert
        links.Single(l => l.Rel == Rels.Self).Href.ShouldBe("/list?at=3");
        links.Single(l => l.Rel == Rels.FirstPage).Href.ShouldBe("/list?at=1");
        links.Single(l => l.Rel == Rels.LastPage).Href.ShouldBe("/list?at=5");
        links.Single(l => l.Rel == Rels.PreviousPage).Href.ShouldBe("/list?at=2");
        links.Single(l => l.Rel == Rels.NextPage).Href.ShouldBe("/list?at=4");
    }

    private static IEnumerable<KeyValuePair<string, string?>> NoFilters(int page) => [];
}
