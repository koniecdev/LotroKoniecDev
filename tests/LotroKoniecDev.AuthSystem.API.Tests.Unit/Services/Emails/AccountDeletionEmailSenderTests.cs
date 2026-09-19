using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails;

/// <summary>
/// The notice #685 sends to the address an armed undo would restore. Every integration test swaps this
/// sender for a spy, so without these the two addresses it juggles are pinned by nothing but a comment
/// — and getting them the wrong way round produces a link that silently resolves to no account.
/// </summary>
public sealed class AccountDeletionEmailSenderTests
{
    private const string PreviousEmail = "bilbo@shire.me";
    private const string CurrentEmail = "frodo@shire.me";

    private static readonly DateTimeOffset FinalizesAt = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly ICancelDeletionLinkFactory _linkFactory = Substitute.For<ICancelDeletionLinkFactory>();

    [Fact]
    public async Task SendDeletionScheduledNoticeToPreviousAddress_BuildsTheLinkFromTheCurrentAddress()
    {
        // CancelAccountDeletion finds the account with FindByEmailAsync on the address in the link, so
        // the link has to name where the account sits now — not the mailbox reading the message. Build
        // it from the recipient instead and the link resolves to nothing, which no Result would show.
        _linkFactory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns("https://auth.test/cancel");
        _emailService.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        AccountDeletionEmailSender sut = CreateSut();

        Result result = await sut.SendDeletionScheduledNoticeToPreviousAddressAsync(
            Guid.NewGuid(), PreviousEmail, CurrentEmail, "cancel-token", FinalizesAt, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _linkFactory.Received(1).Create(CurrentEmail, "cancel-token");
        _linkFactory.DidNotReceive().Create(PreviousEmail, Arg.Any<string>());
    }

    [Fact]
    public async Task SendDeletionScheduledNoticeToPreviousAddress_SendsToThePreviousAddress()
    {
        // The mirror of the test above: the recipient is the armed address, and the whole point of the
        // message is that it does NOT go where the ordinary notice goes.
        _linkFactory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns("https://auth.test/cancel");
        _emailService.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        AccountDeletionEmailSender sut = CreateSut();

        await sut.SendDeletionScheduledNoticeToPreviousAddressAsync(
            Guid.NewGuid(), PreviousEmail, CurrentEmail, "cancel-token", FinalizesAt, CancellationToken.None);

        await _emailService.Received(1).SendAsync(
            PreviousEmail, Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendDeletionScheduledNoticeToPreviousAddress_NamesTheCurrentAddressAndTheDeletionDate()
    {
        // The reader has to learn where their account went and by when it dies, or the message gives
        // them no reason to click anything.
        _linkFactory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns("https://auth.test/cancel");
        EmailBody? captured = null;
        _emailService.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EmailBody>(body => captured = body),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        AccountDeletionEmailSender sut = CreateSut();

        await sut.SendDeletionScheduledNoticeToPreviousAddressAsync(
            Guid.NewGuid(), PreviousEmail, CurrentEmail, "cancel-token", FinalizesAt, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Html.ShouldContain(CurrentEmail);
        captured.Html.ShouldContain("2026-08-18");
        captured.Html.ShouldContain("https://auth.test/cancel");
    }

    [Fact]
    public async Task SendDeletionScheduledNoticeToPreviousAddress_PropagatesASendFailure()
    {
        _linkFactory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns("https://auth.test/cancel");
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailService.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        AccountDeletionEmailSender sut = CreateSut();

        Result result = await sut.SendDeletionScheduledNoticeToPreviousAddressAsync(
            Guid.NewGuid(), PreviousEmail, CurrentEmail, "cancel-token", FinalizesAt, CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    [Fact]
    public async Task SendDeletionScheduledEmail_StillBuildsTheLinkFromTheAddressItSendsTo()
    {
        // The ordinary notice: one address, used for both jobs. It is here so the pair above cannot be
        // read as "the link always names someone else".
        _linkFactory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns("https://auth.test/cancel");
        _emailService.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        AccountDeletionEmailSender sut = CreateSut();

        await sut.SendDeletionScheduledEmailAsync(
            Guid.NewGuid(), CurrentEmail, "cancel-token", FinalizesAt, CancellationToken.None);

        _linkFactory.Received(1).Create(CurrentEmail, "cancel-token");
        await _emailService.Received(1).SendAsync(
            CurrentEmail, Arg.Any<string>(), Arg.Any<EmailBody>(), Arg.Any<CancellationToken>());
    }

    private AccountDeletionEmailSender CreateSut() =>
        new(
            _emailService,
            _linkFactory,
            new EmailTemplateRenderer(
                Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings
                {
                    Issuer = "https://auth.test"
                })),
            NullLogger<AccountDeletionEmailSender>.Instance);
}
