using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Frontend.Infrastructure.Hateoas;

/// <summary>
/// The helper the pages use to decide which actions to show, following TheKittySaver's per-page
/// <c>HasLink</c>. A page decides whether to draw an action, and which <c>href</c> and <c>method</c> to
/// use, from the <c>_links</c> the TMS API already sends (#153), instead of working the role or status
/// out itself.
/// <see cref="HasLink"/> answers whether an action is offered; <see cref="FindLink"/> returns the link,
/// so a write or a state change can be sent to the server's own <c>Href</c>.
/// </summary>
internal static class LinkExtensions
{
    extension(IReadOnlyCollection<LinkDto> links)
    {
        /// <summary>Whether the resource offers the action named by <paramref name="rel"/>.</summary>
        public bool HasLink(string rel) => links.Any(link => link.Rel == rel);

        /// <summary>
        /// The link for <paramref name="rel"/>, or <see langword="null"/> when the resource does not offer
        /// it. Callers read <see cref="LinkDto.Href"/> to follow the server's URL.
        /// </summary>
        public LinkDto? FindLink(string rel) => links.FirstOrDefault(link => link.Rel == rel);
    }
}
