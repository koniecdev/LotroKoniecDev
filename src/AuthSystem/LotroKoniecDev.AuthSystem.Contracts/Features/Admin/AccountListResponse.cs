using LotroKoniecDev.Hateoas.Abstractions;

namespace LotroKoniecDev.AuthSystem.Contracts.Features.Admin;

/// <summary>
/// One page of the accounts the admin manages (#1488). Erased accounts are left out.
/// </summary>
public sealed record AccountListResponse(
    IReadOnlyList<AccountSummaryResponse> Items,
    int Page,
    int PageSize,
    int TotalCount) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}

public sealed record AccountSummaryResponse(
    Guid UserId,
    string Username,
    string Email,
    IReadOnlyList<string> Roles,
    DateTimeOffset? DeletionScheduledAt) : ILinksResponse
{
    public IReadOnlyCollection<LinkDto> Links { get; set; } = [];
}
