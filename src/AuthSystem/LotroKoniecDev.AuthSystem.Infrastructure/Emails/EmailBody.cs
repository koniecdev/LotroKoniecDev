namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

/// <summary>
/// The two forms of one message. We always send both, as <c>multipart/alternative</c>. Mail that is
/// HTML only looks worse to spam filters and cannot be read in a text-only client.
/// </summary>
public sealed record EmailBody(string Html, string PlainText);
