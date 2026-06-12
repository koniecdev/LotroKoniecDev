namespace LotroKoniecDev.TranslationSystem.Contracts.Common;

public sealed record PaginationAndMultipleSorting(int Page = 1, int PageSize = 10, string? Sort = null)
    : IPaginationable, ISortable
{
    public int Page { get; } = Math.Max(Page, 1);
    public int PageSize { get; } = Math.Clamp(PageSize, 1, 100);
}
