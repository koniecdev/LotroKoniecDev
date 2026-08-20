using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// The revert handler decides whether a stolen account can be taken back, so every refusal it makes is
/// pinned here. It is the one leg of the flow with no database dependency, which keeps these pure.
/// </summary>
public sealed class RevertEmailChangeHandlerTests
{
    private const string PreviousEmail = "frodo@shire.me";
    private const string CurrentEmail = "attacker@mordor.example";

    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IUserSessionRevoker _sessionRevoker = Substitute.For<IUserSessionRevoker>();

    public RevertEmailChangeHandlerTests()
    {
        _userManager.NormalizeEmail(Arg.Any<string?>())
            .Returns(callInfo => callInfo.Arg<string?>()?.ToUpperInvariant());
    }

    [Fact]
    public async Task Handle_MalformedUserId_RefusesWithoutTouchingTheStore()
    {
        // Identity converts the id to the key type before it queries, so a non-GUID would throw inside
        // the store. The validator has to stop it first.
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor("not-a-guid"), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        await _userManager.DidNotReceiveWithAnyArgs().FindByIdAsync(default!);
    }

    [Fact]
    public async Task Handle_UnknownUser_RefusesWithTheSameErrorAsABadToken()
    {
        Guid userId = Guid.NewGuid();
        _userManager.FindByIdAsync(userId.ToString()).Returns((ApplicationUser?)null);
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor(userId.ToString()), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.InvalidEmailChangeToken");
    }

    [Fact]
    public async Task Handle_InvalidToken_RefusesAndLeavesTheAccountAlone()
    {
        ApplicationUser user = CreateUser();
        StubUser(user, tokenValid: false);
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor(user.Id.ToString()), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        user.Email.ShouldBe(CurrentEmail);
        user.PasswordHash.ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_AccountAlreadyBackOnThePreviousAddress_RefusesSoASecondClickDoesNothing()
    {
        // The token carries no security stamp, so this check is what stops a replay from clearing a
        // password the owner has already reset.
        ApplicationUser user = CreateUser();
        user.Email = PreviousEmail;
        StubUser(user, tokenValid: true);
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor(user.Id.ToString()), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        user.PasswordHash.ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_AccountMovedOnAgain_StillRevertsToThePreviousAddress()
    {
        // The takeover this guard exists for: the attacker changes the address twice, so the account no
        // longer sits where the token was issued against. The link still has to work.
        ApplicationUser user = CreateUser();
        user.Email = "third@example.com";
        StubUser(user, tokenValid: true);
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor(user.Id.ToString()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        user.Email.ShouldBe(PreviousEmail);
        user.PasswordHash.ShouldBeNull();
    }

    [Fact]
    public async Task Handle_PreviousAddressTakenByAnotherAccount_RefusesAndKeepsThePassword()
    {
        // There is nowhere to go back to. Clearing the password anyway would lock the account out of
        // both addresses at once.
        ApplicationUser user = CreateUser();
        StubUser(user, tokenValid: true);
        _userManager.FindByEmailAsync(PreviousEmail).Returns(CreateUser());
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor(user.Id.ToString()), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.UserAlreadyExistsByEmail");
        user.Email.ShouldBe(CurrentEmail);
        user.PasswordHash.ShouldNotBeNull();
    }

    [Fact]
    public async Task Handle_ValidToken_RestoresTheAddressConfirmsItAndEndsEverySession()
    {
        ApplicationUser user = CreateUser();
        StubUser(user, tokenValid: true);
        string stampBefore = user.SecurityStamp!;
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor(user.Id.ToString()), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.RestoredEmail.ShouldBe(PreviousEmail);
        user.Email.ShouldBe(PreviousEmail);
        user.EmailConfirmed.ShouldBeTrue();
        user.PasswordHash.ShouldBeNull();
        user.SecurityStamp.ShouldNotBe(stampBefore);

        // Revoking the OpenIddict artifacts leaves no trace in the return value, so it is asserted here.
        await _sessionRevoker.Received(1).RevokeAllAsync(user.Id.ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_PersistenceRefusesTheUpdate_ReportsFailure()
    {
        ApplicationUser user = CreateUser();
        StubUser(user, tokenValid: true);
        _userManager.UpdateAsync(user).Returns(IdentityResult.Failed(new IdentityError
        {
            Code = "ConcurrencyFailure",
            Description = "Optimistic concurrency failure."
        }));
        RevertEmailChange.Handler sut = CreateSut();

        SharedKernel.Monads.Result<RevertEmailChange.RevertedEmailChange> result = await sut.Handle(
            CommandFor(user.Id.ToString()), CancellationToken.None);

        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.EmailChangeFailed");
    }

    private static RevertEmailChange.Command CommandFor(string userId) =>
        new(userId, PreviousEmail, CurrentEmail, "revert-token", "203.0.113.7", "xunit");

    private void StubUser(ApplicationUser user, bool tokenValid)
    {
        _userManager.FindByIdAsync(user.Id.ToString()).Returns(user);
        _userManager.FindByEmailAsync(PreviousEmail).Returns((ApplicationUser?)null);
        _userManager.VerifyUserTokenAsync(
                user,
                EmailChangeRevertTokenProvider.ProviderName,
                EmailChangeRevertTokenProvider.PurposeFor(PreviousEmail, CurrentEmail),
                Arg.Any<string>())
            .Returns(tokenValid);
        _userManager.UpdateAsync(user).Returns(IdentityResult.Success);
        _userManager.GeneratePasswordResetTokenAsync(user).Returns("reset-token");
    }

    private RevertEmailChange.Handler CreateSut() =>
        new(
            _userManager,
            _sessionRevoker,
            new RevertEmailChange.CommandValidator(),
            NullLogger<RevertEmailChange.Handler>.Instance);

    private static ApplicationUser CreateUser() =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = CurrentEmail,
            PasswordHash = "hashed",
            SecurityStamp = Guid.NewGuid().ToString()
        };

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
