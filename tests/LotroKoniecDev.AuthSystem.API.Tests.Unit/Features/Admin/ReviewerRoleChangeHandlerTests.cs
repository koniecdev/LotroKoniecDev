using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Features.Admin;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Authorization;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Admin;

/// <summary>
/// The branch no endpoint test reaches: Identity refuses the role write (#1488). The change did not
/// happen, so no session may end; the revoker is asserted because that leaves no trace in the result.
/// </summary>
public sealed class ReviewerRoleChangeHandlerTests
{
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();
    private readonly IUserSessionRevoker _sessionRevoker = Substitute.For<IUserSessionRevoker>();
    private readonly ApplicationUser _account = new() { Id = Guid.NewGuid(), UserName = "frodo" };

    [Fact]
    public async Task Grant_WhenIdentityRefusesTheWrite_FailsAndEndsNoSession()
    {
        // Arrange
        StubAccount(AuthConstants.Roles.Translator);
        _userManager.AddToRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .Returns(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        GrantReviewerRole.Handler handler = new(
            _userManager, _sessionRevoker, new GrantReviewerRole.CommandValidator(), NullLogger<GrantReviewerRole.Handler>.Instance);

        // Act
        Result result = await handler.Handle(new GrantReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.ReviewerRoleChangeFailed");
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    [Fact]
    public async Task Remove_WhenIdentityRefusesTheWrite_FailsAndEndsNoSession()
    {
        // Arrange
        StubAccount(AuthConstants.Roles.Translator, AuthConstants.Roles.Reviewer);
        _userManager.IsInRoleAsync(_account, AuthConstants.Roles.Reviewer).Returns(true);
        _userManager.RemoveFromRoleAsync(_account, AuthConstants.Roles.Reviewer)
            .Returns(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        RemoveReviewerRole.Handler handler = new(
            _userManager, _sessionRevoker, new RemoveReviewerRole.CommandValidator(), NullLogger<RemoveReviewerRole.Handler>.Instance);

        // Act
        Result result = await handler.Handle(new RemoveReviewerRole.Command("admin", _account.Id), CancellationToken.None);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe("Auth.ReviewerRoleChangeFailed");
        await _sessionRevoker.DidNotReceiveWithAnyArgs().RevokeAllAsync(default!);
    }

    private void StubAccount(params string[] roles)
    {
        _userManager.FindByIdAsync(_account.Id.ToString()).Returns(_account);
        _userManager.GetRolesAsync(_account).Returns(roles.ToList());
    }

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
