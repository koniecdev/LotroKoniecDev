namespace LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

/// <summary>
/// What a message says, with nothing about how it looks. Senders build this, and
/// <see cref="IEmailTemplateRenderer"/> turns it into the HTML and plain-text bodies.
/// </summary>
internal sealed record EmailTemplateModel
{
    /// <summary>The line most mail clients show next to the subject in the message list.</summary>
    public required string Preheader { get; init; }

    public required string Heading { get; init; }

    public required IReadOnlyList<string> Paragraphs { get; init; }

    public EmailCallToAction? CallToAction { get; init; }

    /// <summary>What the reader should do if they did not ask for this message.</summary>
    public string? SecurityNote { get; init; }
}
