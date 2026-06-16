using LotroKoniecDev.Frontend.Infrastructure.Hateoas;
using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Frontend.Tests.Unit.Infrastructure.Hateoas;

public sealed class LinkExtensionsTests
{
    private static readonly IReadOnlyCollection<LinkDto> Links =
    [
        new LinkDto("https://tms.example/api/v1/translations/1", "self", "GET"),
        new LinkDto("https://tms.example/api/v1/translations", "upsert", "PUT")
    ];

    [Theory]
    [InlineData("self")]
    [InlineData("upsert")]
    public void HasLink_WhenRelPresent_ReturnsTrue(string rel)
    {
        Links.HasLink(rel).ShouldBeTrue();
    }

    [Fact]
    public void HasLink_WhenRelAbsent_ReturnsFalse()
    {
        Links.HasLink("approve").ShouldBeFalse();
    }

    [Fact]
    public void HasLink_OnEmptyCollection_ReturnsFalse()
    {
        Array.Empty<LinkDto>().HasLink("self").ShouldBeFalse();
    }

    [Fact]
    public void FindLink_WhenRelPresent_ReturnsTheMatchingLinkSoCallersCanFollowItsHref()
    {
        LinkDto? link = Links.FindLink("upsert");

        link.ShouldNotBeNull();
        link.Href.ShouldBe("https://tms.example/api/v1/translations");
        link.Method.ShouldBe("PUT");
    }

    [Fact]
    public void FindLink_WhenRelAbsent_ReturnsNull()
    {
        Links.FindLink("approve").ShouldBeNull();
    }
}
