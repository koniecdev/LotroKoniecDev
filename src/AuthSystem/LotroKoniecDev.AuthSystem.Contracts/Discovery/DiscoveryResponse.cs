using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.Contracts.Discovery;

public sealed record DiscoveryResponse(string Name) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
