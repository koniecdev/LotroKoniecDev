using LotroKoniecDev.Hateoas.Abstractions;
using LotroKoniecDev.Hateoas.LinkFactories;
using LotroKoniecDev.TranslationSystem.API.Hateoas.PaginationLinkFactories;
using LotroKoniecDev.TranslationSystem.Contracts.Common;
using LotroKoniecDev.TranslationSystem.Contracts.Hateoas;
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
        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response);

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
        // The pager is hidden entirely in this state, so any boundary rel here is a control the user
        // could click to reload the page they are already on (#545).
        PaginationResponse<string> response = new()
        {
            Items = [],
            Page = 1,
            PageSize = 50,
            TotalCount = 3
        };

        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response);

        links.ShouldHaveSingleItem().Rel.ShouldBe(Rels.Self);
    }

    [Fact]
    public async Task CreatePaginationLinks_OnAnOverRangePage_KeepsBothAbsoluteJumpsSoTheCallerCanGetBack()
    {
        // Page is clamped at the lower bound only, so ?page=99 of 3 is reachable. previous/next are
        // relative and next is correctly absent; first/last are absolute and stay the way back.
        PaginationResponse<string> response = new()
        {
            Items = [],
            Page = 99,
            PageSize = 1,
            TotalCount = 3
        };

        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response);

        links.Select(l => l.Rel).ShouldBe([Rels.Self, Rels.FirstPage, Rels.LastPage, Rels.PreviousPage], ignoreOrder: true);
    }

    [Fact]
    public async Task CreatePaginationLinks_WhenTheLinkFactoryRefusesARel_OmitsItRatherThanEmittingNull()
    {
        // ILinkFactory returns null when the caller would be rejected by the target endpoint's own
        // policy (ADR-0040); the pagination set must degrade to what is left.
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

        IReadOnlyList<LinkDto> links = await _sut.CreatePaginationLinksAsync(EndpointName, response);

        links.Any(l => l.Rel == Rels.LastPage).ShouldBeFalse();
        links.Any(l => l.Rel == Rels.NextPage).ShouldBeTrue();
    }
}
