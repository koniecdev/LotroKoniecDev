namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

/// <summary>
/// Both forms always go out as <c>multipart/alternative</c>: HTML-only mail looks worse to spam filters
/// and cannot be read in a text-only client.
/// </summary>
public sealed record EmailBody(string Html, string PlainText);
