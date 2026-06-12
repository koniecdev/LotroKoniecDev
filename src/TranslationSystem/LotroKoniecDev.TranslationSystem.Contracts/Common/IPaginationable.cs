namespace LotroKoniecDev.TranslationSystem.Contracts.Common;

public interface IPaginationable
{
    int Page { get; }
    int PageSize { get; }
}
