using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Frontend.Infrastructure.Hateoas;

/// <summary>
/// The Frontend's link-driven affordance helper (mirrors TheKittySaver's per-page <c>HasLink</c>):
/// the pages decide whether to render an action — and which <c>href</c> / <c>method</c> to follow —
/// from the hypermedia <c>_links</c> the TMS API already emits (#153), instead of recomputing role or
/// status locally. <see cref="HasLink"/> gates an affordance by rel presence; <see cref="FindLink"/>
/// returns the matched link so a write / state-transition action can be issued against its server
/// <c>Href</c>.
/// </summary>
internal static class LinkExtensions
{
    extension(IReadOnlyCollection<LinkDto> links)
    {
        /// <summary>Whether the resource advertises the affordance identified by <paramref name="rel"/>.</summary>
        public bool HasLink(string rel) => links.Any(link => link.Rel == rel);

        /// <summary>
        /// The link for <paramref name="rel"/>, or <see langword="null"/> when the resource does not
        /// advertise it — callers read <see cref="LinkDto.Href"/> to follow the server's URI.
        /// </summary>
        public LinkDto? FindLink(string rel) => links.FirstOrDefault(link => link.Rel == rel);
    }
}
