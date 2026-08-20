using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

public interface ILinkFactory
{
    /// <summary>
    /// Builds the link for a named endpoint. Returns <see langword="null"/> when the endpoint cannot
    /// be found, or when the endpoint's own authorization would reject the current caller.
    /// </summary>
    ValueTask<LinkDto?> CreateAsync(string endpoint, string rel, string method, object? values = null);
}
