using System.Collections.Concurrent;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;

namespace LotroKoniecDev.AuthSystem.API.Tests.Integration.Shared;

/// <summary>
/// Keeps every sign-up mail with the inbox it went to, and a test reads only its own account's inbox.
/// Most of these mails travel through the outbox after the request has ended, so a mail from an earlier
/// test can still arrive after <see cref="Reset"/> and must not pass for the test's own (#950). A resend
/// is the exception: it sends inside the request, to the address as it was typed.
/// </summary>
#pragma warning disable CA1515
public sealed class SpyAccountConfirmationEmailSender : IAccountConfirmationEmailSender
#pragma warning restore CA1515
{
    private readonly ConcurrentQueue<ConfirmationEmail> _confirmationEmails = new();
    private readonly ConcurrentQueue<string> _registrationAttemptedRecipients = new();

    public bool ShouldFail { get; set; }

    /// <summary>
    /// Counts the mails to every inbox, a late one from an earlier test included. Use it only when the
    /// test has no account whose inbox it could read, such as a payload that names no account, and only
    /// on a host where every test that shares this spy waits for each confirmation mail it causes.
    /// </summary>
    public int ConfirmationEmailCountToAnyInbox => _confirmationEmails.Count;

    public Task<Result> SendEmailConfirmationAsync(Guid userId, string email, string confirmationToken, CancellationToken cancellationToken)
    {
        _confirmationEmails.Enqueue(new ConfirmationEmail(userId, email, confirmationToken));

        return ShouldFail
            ? Task.FromResult(Result.Failure(new Error("Test.EmailFailed", "Simulated email failure")))
            : Task.FromResult(Result.Success());
    }

    public Task<Result> SendRegistrationAttemptedNoticeAsync(Guid userId, string email, CancellationToken cancellationToken)
    {
        _registrationAttemptedRecipients.Enqueue(email);
        return Task.FromResult(Result.Success());
    }

    public IReadOnlyList<ConfirmationEmail> ConfirmationEmailsTo(string recipient) =>
        _confirmationEmails.Where(mail => mail.Recipient == recipient).ToList();

    /// <summary>
    /// The token from the newest confirmation mail to this inbox, or null when none came.
    /// </summary>
    public string? LastConfirmationTokenSentTo(string recipient) =>
        ConfirmationEmailsTo(recipient).LastOrDefault()?.ConfirmationToken;

    public int RegistrationAttemptedNoticeCountTo(string recipient) =>
        _registrationAttemptedRecipients.Count(address => address == recipient);

    /// <summary>
    /// Throws when the time runs out. Any mail since the last <see cref="Reset"/> counts, so a test that
    /// expects a second mail to one inbox resets in between.
    /// </summary>
    public Task WaitForCaptureAsync(string recipient, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _confirmationEmails.Any(mail => mail.Recipient == recipient),
            $"A confirmation mail to {recipient}",
            timeout);

    /// <summary>
    /// The notice to the owner of a taken address; it travels the outbox pipeline too (#860).
    /// </summary>
    public Task WaitForRegistrationAttemptedCaptureAsync(string recipient, TimeSpan? timeout = null) =>
        MailWait.UntilAsync(
            () => _registrationAttemptedRecipients.Contains(recipient),
            $"A registration-attempted notice to {recipient}",
            timeout);

    public void Reset()
    {
        _confirmationEmails.Clear();
        _registrationAttemptedRecipients.Clear();
        ShouldFail = false;
    }

    public sealed record ConfirmationEmail(Guid UserId, string Recipient, string ConfirmationToken);
}
