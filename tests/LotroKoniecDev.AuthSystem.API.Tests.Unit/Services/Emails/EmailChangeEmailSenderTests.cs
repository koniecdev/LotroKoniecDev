using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails;

/// <summary>
/// Every integration test swaps this sender for a spy, so the Polish it writes is pinned only here.
/// </summary>
public sealed class EmailChangeEmailSenderTests
{
    private const string PreviousEmail = "bilbo@shire.me";
    private const string NewEmail = "frodo@shire.me";

    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailChangeVerificationLinkFactory _verificationLinkFactory =
        Substitute.For<IEmailChangeVerificationLinkFactory>();
    private readonly IEmailChangeRevertLinkFactory _revertLinkFactory = Substitute.For<IEmailChangeRevertLinkFactory>();

    [Theory]
    [InlineData(14 * 24, "Link działa przez 14 dni od wysłania tej wiadomości.")]
    [InlineData(24, "Link działa przez 24 godziny od wysłania tej wiadomości.")]
    [InlineData(1, "Link działa przez 1 godzinę od wysłania tej wiadomości.")]
    public async Task SendChangedNoticeWithRevert_NamesTheRevertWindowInTheFormThatFollowsPrzez(
        int revertWindowHours,
        string expected)
    {
        // "przez" needs other forms than the "wygasa po" the other e-mails use. With those, the undo
        // offer read "przez 14 dniach" (#1032).
        _revertLinkFactory.Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("https://auth.test/revert");
        EmailBody? captured = null;
        _emailService.SendAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Do<EmailBody>(body => captured = body),
                Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeEmailSender sut = CreateSut();

        await sut.SendChangedNoticeWithRevertAsync(
            Guid.NewGuid(), PreviousEmail, NewEmail, "revert-token", TimeSpan.FromHours(revertWindowHours),
            CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.PlainText.ShouldContain(expected);
        captured.Html.ShouldContain(expected);
    }

    private EmailChangeEmailSender CreateSut() =>
        new(
            _emailService,
            _verificationLinkFactory,
            _revertLinkFactory,
            new EmailTemplateRenderer(
                Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings
                {
                    Issuer = "https://auth.test"
                })),
            Microsoft.Extensions.Options.Options.Create(new EmailChangeTokenProviderOptions()),
            NullLogger<EmailChangeEmailSender>.Instance);
}
