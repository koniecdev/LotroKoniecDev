using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Frontend.Infrastructure.Hateoas;

/// <summary>
/// Pages decide which actions to show from the links the API sends, never from the role or status
/// (ADR-0040).
/// </summary>
internal static class LinkExtensions
{
    extension(IReadOnlyCollection<LinkDto> links)
    {
        public bool HasLink(string rel) => links.Any(link => link.Rel == rel);

        public LinkDto? FindLink(string rel) => links.FirstOrDefault(link => link.Rel == rel);
    }
}
