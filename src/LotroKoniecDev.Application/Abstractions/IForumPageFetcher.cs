namespace LotroKoniecDev.Application.Abstractions;

/// <summary>
/// Fetches the LOTRO release notes forum page.
/// </summary>
public interface IForumPageFetcher
{
    Task<Result<string>> FetchReleaseNotesPageAsync();
}
