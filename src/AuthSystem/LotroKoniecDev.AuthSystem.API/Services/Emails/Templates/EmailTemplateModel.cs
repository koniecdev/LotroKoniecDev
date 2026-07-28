namespace LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

/// <summary>
/// What a transactional message says, with no trace of how it looks. Senders build this;
/// <see cref="IEmailTemplateRenderer"/> turns it into the HTML and plain-text bodies.
/// </summary>
internal sealed record EmailTemplateModel
{
    /// <summary>Line most inbox lists preview next to the subject.</summary>
    public required string Preheader { get; init; }

    public required string Heading { get; init; }

    public required IReadOnlyList<string> Paragraphs { get; init; }

    public EmailCallToAction? CallToAction { get; init; }

    /// <summary>What to do when the recipient did not trigger the message.</summary>
    public string? SecurityNote { get; init; }
}
