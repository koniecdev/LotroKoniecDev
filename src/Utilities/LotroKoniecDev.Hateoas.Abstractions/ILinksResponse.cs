namespace LotroKoniecDev.Hateoas.Abstractions;

public interface ILinksResponse
{
    IReadOnlyCollection<LinkDto> Links { get; set; }
}
