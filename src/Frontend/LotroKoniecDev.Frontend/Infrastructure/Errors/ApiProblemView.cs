namespace LotroKoniecDev.Frontend.Infrastructure.Errors;

/// <summary>
/// What a page shows for one failed call.
/// </summary>
/// <param name="Message">The Polish headline.</param>
/// <param name="SecondaryMessage">
/// The extra line under the headline. Only a problem the Frontend wrote has one: it writes a short
/// Polish title and a longer Polish detail, and both are meant for the user. An API's extra text is
/// English and travels in <paramref name="TechnicalDetail"/> instead.
/// </param>
/// <param name="TechnicalDetail">
/// The API's error code and its own English wording, hidden behind a toggle so a bug report can quote
/// it. Null for a problem the Frontend wrote, which has nothing to hide.
/// </param>
/// <param name="UnmappedErrorCode">
/// Set only when the API sent a code <see cref="ApiProblemCopy"/> has no text for, so the page can log
/// the gap.
/// </param>
internal sealed record ApiProblemView(
    string Message,
    string? SecondaryMessage,
    string? TechnicalDetail,
    string? UnmappedErrorCode);
