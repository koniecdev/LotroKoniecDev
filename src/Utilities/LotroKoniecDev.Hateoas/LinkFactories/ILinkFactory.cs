using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

public interface ILinkFactory
{
    LinkDto? Create(string endpoint, string rel, string method, object? values = null);
}
