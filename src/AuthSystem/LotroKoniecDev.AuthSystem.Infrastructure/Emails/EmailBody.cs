namespace LotroKoniecDev.AuthSystem.Infrastructure.Emails;

/// <summary>
/// The two alternative representations of one message. Both are always sent, as
/// <c>multipart/alternative</c>: HTML-only transactional mail scores badly with spam filters and
/// is unreadable in text-only clients.
/// </summary>
public sealed record EmailBody(string Html, string PlainText);
