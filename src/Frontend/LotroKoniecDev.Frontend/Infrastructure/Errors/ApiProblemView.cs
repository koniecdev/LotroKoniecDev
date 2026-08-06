namespace LotroKoniecDev.Frontend.Infrastructure.Errors;

/// <summary>
/// What a page shows for one failed call.
/// </summary>
/// <param name="Message">The Polish headline.</param>
/// <param name="SecondaryMessage">
/// The elaboration under the headline. Only a Frontend-authored problem has one — it writes a short
/// Polish title plus a longer Polish detail, and both are already user-facing. An API-authored
/// problem's elaboration is English and travels in <paramref name="TechnicalDetail"/> instead.
/// </param>
/// <param name="TechnicalDetail">
/// The API's error code and its own English wording, kept collapsible so a bug report can quote it.
/// Null for a Frontend-authored problem, which has nothing to hide.
/// </param>
/// <param name="UnmappedErrorCode">
/// Set only when the API sent a code <see cref="ApiProblemCopy"/> has no copy for, so the renderer
/// can log the gap.
/// </param>
internal sealed record ApiProblemView(
    string Message,
    string? SecondaryMessage,
    string? TechnicalDetail,
    string? UnmappedErrorCode);
