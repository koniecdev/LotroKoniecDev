using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

public interface ILinkFactory
{
    /// <summary>
    /// Builds the link for a named endpoint, or <see langword="null"/> when the endpoint cannot be
    /// resolved or the current caller would be rejected by that endpoint's own authorization.
    /// </summary>
    ValueTask<LinkDto?> CreateAsync(string endpoint, string rel, string method, object? values = null);
}
