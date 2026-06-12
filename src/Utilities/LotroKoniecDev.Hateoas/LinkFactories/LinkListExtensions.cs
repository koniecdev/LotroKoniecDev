using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.Hateoas.LinkFactories;

public static class LinkListExtensions
{
    extension(List<LinkDto> links)
    {
        public void AddIfPresent(LinkDto? link)
        {
            if (link is not null)
            {
                links.Add(link);
            }
        }
    }
}
