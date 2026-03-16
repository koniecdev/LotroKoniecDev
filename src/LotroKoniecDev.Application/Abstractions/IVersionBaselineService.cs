using LotroKoniecDev.Domain.Models;

namespace LotroKoniecDev.Application.Abstractions;

public interface IVersionBaselineService
{
    Result SaveBaseline(
        DatVersionInfo datVersion,
        string? forumVersion,
        string translationFilePath,
        string versionFilePath);
}
