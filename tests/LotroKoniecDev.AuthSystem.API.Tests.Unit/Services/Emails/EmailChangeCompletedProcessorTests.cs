using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Outbox;
using LotroKoniecDev.AuthSystem.API.Services.Emails;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.BuildingBlocks;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails;

public sealed class EmailChangeCompletedProcessorTests
{
    private const string PreviousEmail = "frodo@shire.me";
    private const string NewEmail = "frodo@rivendell.example";

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IEmailChangeEmailSender _emailSender = Substitute.For<IEmailChangeEmailSender>();

    public EmailChangeCompletedProcessorTests()
    {
        // Without this the substitute answers null on both sides of every address comparison, and
        // string.Equals(null, null) is true — so the arming check would silently pass in every test.
        _userManager.NormalizeEmail(Arg.Any<string?>())
            .Returns(callInfo => callInfo.Arg<string?>()?.ToUpperInvariant());
    }

    [Fact]
    public async Task ProcessAsync_UserNoLongerExists_SucceedsWithoutSending()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        EmailChangeCompletedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeCompleted(userId, PreviousEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendChangedNoticeWithRevertAsync(default, default!, default!, default!, default, default);
        await _emailSender.DidNotReceiveWithAnyArgs().SendChangedNoticeAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_ChangeCompleted_SendsTheRevertOfferToThePreviousAddressAndANoticeToTheNewOne()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.GenerateUserTokenAsync(
                user,
                EmailChangeRevertTokenProvider.ProviderName,
                EmailChangeRevertTokenProvider.PurposeFor(PreviousEmail, NewEmail))
            .Returns("fresh-revert-token");
        _emailSender.SendChangedNoticeWithRevertAsync(
                user.Id, PreviousEmail, NewEmail, "fresh-revert-token", TimeSpan.FromDays(14), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _emailSender.SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeCompletedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeCompleted(user.Id, PreviousEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1).SendChangedNoticeWithRevertAsync(
            user.Id, PreviousEmail, NewEmail, "fresh-revert-token", TimeSpan.FromDays(14), Arg.Any<CancellationToken>());
        await _emailSender.Received(1)
            .SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_RevertOfferFails_AsksForARetryAndDoesNotSendTheOtherNotice()
    {
        // The revert offer goes first on purpose. It is the one message that can still undo a takeover,
        // so if only one of the two ever gets through it has to be that one.
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendChangedNoticeWithRevertAsync(
                user.Id, PreviousEmail, NewEmail, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        EmailChangeCompletedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeCompleted(user.Id, PreviousEmail, NewEmail), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
        await _emailSender.DidNotReceiveWithAnyArgs().SendChangedNoticeAsync(default, default!, default!, default);
    }

    [Fact]
    public async Task ProcessAsync_NoticeToTheNewAddressFails_AsksForARetry()
    {
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        Error smtpError = new("Email.SendFailed", "SMTP relay refused the message.");
        _emailSender.SendChangedNoticeWithRevertAsync(
                user.Id, PreviousEmail, NewEmail, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _emailSender.SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Failure(smtpError));
        EmailChangeCompletedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeCompleted(user.Id, PreviousEmail, NewEmail), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(smtpError);
    }

    [Fact]
    public async Task ProcessAsync_RunTwice_BuildsTheRevertTokenFromThePayloadBothTimes()
    {
        // The previous address is gone from the user row by now, so a processor that read it from
        // there would build a different purpose on a redelivery and hand out a link that cannot work.
        ApplicationUser user = CreateUser();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _emailSender.SendChangedNoticeWithRevertAsync(
                user.Id, PreviousEmail, NewEmail, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _emailSender.SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeCompletedProcessor sut = CreateSut();
        EmailChangeCompleted message = new(user.Id, PreviousEmail, NewEmail);

        Result first = await sut.ProcessAsync(message, CancellationToken.None);
        Result second = await sut.ProcessAsync(message, CancellationToken.None);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        await _userManager.Received(2).GenerateUserTokenAsync(
            user,
            EmailChangeRevertTokenProvider.ProviderName,
            EmailChangeRevertTokenProvider.PurposeFor(PreviousEmail, NewEmail));
    }

    [Fact]
    public async Task ProcessAsync_AnEarlierChangeAlreadyArmedTheUndo_SendsTheNoticeWithoutALink()
    {
        // The second hop of A -> B -> C. The armed target is still A, so this message must hand B
        // nothing: B is whoever took the account over, and a link there undoes the owner's recovery.
        ApplicationUser user = CreateUser();
        user.EmailChangeRevertTo = "someone-else@shire.me";
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _emailSender.SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeCompletedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeCompleted(user.Id, PreviousEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendChangedNoticeWithRevertAsync(default, default!, default!, default!, default, default);
        await _emailSender.Received(1)
            .SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_NothingArmedAtAll_SendsTheNoticeWithoutALink()
    {
        ApplicationUser user = CreateUser();
        user.EmailChangeRevertTo = null;
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _emailSender.SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeCompletedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeCompleted(user.Id, PreviousEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.DidNotReceiveWithAnyArgs()
            .SendChangedNoticeWithRevertAsync(default, default!, default!, default!, default, default);
    }

    [Fact]
    public async Task ProcessAsync_ArmedTargetDiffersOnlyByCase_StillSendsTheLink()
    {
        ApplicationUser user = CreateUser();
        user.EmailChangeRevertTo = PreviousEmail.ToUpperInvariant();
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _emailSender.SendChangedNoticeWithRevertAsync(
                user.Id, PreviousEmail, NewEmail, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _emailSender.SendChangedNoticeAsync(user.Id, NewEmail, PreviousEmail, Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        EmailChangeCompletedProcessor sut = CreateSut();

        Result result = await sut.ProcessAsync(
            new EmailChangeCompleted(user.Id, PreviousEmail, NewEmail), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _emailSender.Received(1).SendChangedNoticeWithRevertAsync(
            user.Id, PreviousEmail, NewEmail, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("this is not json at all")]
    [InlineData("{}")]
    [InlineData("""{"IdentityUserId":"not-a-guid"}""")]
    [InlineData("""{"IdentityUserId":"0195f0f6-0000-7000-8000-000000000000","PreviousEmail":"","NewEmail":"a@b.pl"}""")]
    [InlineData("""{"IdentityUserId":"0195f0f6-0000-7000-8000-000000000000","PreviousEmail":"a@b.pl","NewEmail":"  "}""")]
    public void TryDeserialize_PoisonPayload_ReturnsTheNullVerdict(string poisonPayload)
    {
        EmailChangeCompletedProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(System.Text.Encoding.UTF8.GetBytes(poisonPayload));

        message.ShouldBeNull();
    }

    [Fact]
    public void TryDeserialize_ValidPayload_ReturnsTheTypedMessage()
    {
        Guid userId = Guid.CreateVersion7();
        EmailChangeCompletedProcessor sut = CreateSut();

        object? message = sut.TryDeserialize(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                new EmailChangeCompleted(userId, PreviousEmail, NewEmail)));

        EmailChangeCompleted typed = message.ShouldBeOfType<EmailChangeCompleted>();
        typed.IdentityUserId.ShouldBe(userId);
        typed.PreviousEmail.ShouldBe(PreviousEmail);
        typed.NewEmail.ShouldBe(NewEmail);
    }

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = NewEmail,
            EmailChangeRevertTo = PreviousEmail
        };

    private EmailChangeCompletedProcessor CreateSut() =>
        new(
            _userManager,
            _emailSender,
            Microsoft.Extensions.Options.Options.Create(new EmailChangeRevertTokenProviderOptions()),
            NullLogger<EmailChangeCompletedProcessor>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
