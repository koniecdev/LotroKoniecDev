using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Every account page, found by reflection so a theory can list them. A test in
/// <c>SecurityHeadersTests</c> proves the list matches the pages the app really maps, so a new page
/// cannot slip past the suites that check every page.
/// </summary>
public static class AccountPages
{
    public static readonly IReadOnlyList<string> Paths = typeof(Program).Assembly.GetTypes()
        .Where(type => type.IsSubclassOf(typeof(PageModel))
            && type is { Namespace: string pageNamespace }
            && pageNamespace.EndsWith(".Pages.Account", StringComparison.Ordinal))
        .Select(type => "/Account/" + type.Name[..^"Model".Length])
        .Order(StringComparer.Ordinal)
        .ToList();

    public static TheoryData<string> All { get; } = new(Paths);
}
