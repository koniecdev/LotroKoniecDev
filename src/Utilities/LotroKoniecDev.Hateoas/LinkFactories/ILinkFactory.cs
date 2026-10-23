using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

public interface ILinkFactory
{
    /// <summary>
    /// Returns <see langword="null"/> when the endpoint cannot be found (a typo in the name, or a missing
    /// <c>WithName(...)</c> on the route), or when the endpoint's own authorization would refuse the
    /// current caller.
    /// </summary>
    ValueTask<LinkDto?> CreateAsync(string endpoint, string rel, string method, object? values = null);
}
