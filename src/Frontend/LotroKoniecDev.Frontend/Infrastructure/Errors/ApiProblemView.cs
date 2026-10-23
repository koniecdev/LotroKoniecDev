namespace LotroKoniecDev.Frontend.Infrastructure.Errors;

/// <param name="Message">The Polish headline.</param>
/// <param name="SecondaryMessage">
/// Only a problem the Frontend wrote has one. An API's extra text is English and travels in
/// <paramref name="TechnicalDetail"/> instead.
/// </param>
/// <param name="TechnicalDetail">
/// The API's error code and English wording, behind a toggle so a bug report can quote it. Usually null
/// (#703).
/// </param>
/// <param name="UnmappedErrorCode">
/// Set only for a code <see cref="ApiProblemCopy"/> has no text for, so the page can log the gap.
/// </param>
internal sealed record ApiProblemView(
    string Message,
    string? SecondaryMessage,
    string? TechnicalDetail,
    string? UnmappedErrorCode);
