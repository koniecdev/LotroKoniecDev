using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using LotroKoniecDev.AuthSystem.API.Features.Auth;
using LotroKoniecDev.AuthSystem.Contracts.Features.Auth.Account;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.SharedKernel.Monads;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Features.Auth;

/// <summary>
/// The default policy keeps a service token away from this handler (#966). The handler still never hands
/// a non-GUID id to Identity, which throws on one, so the crash cannot come back through another caller.
/// </summary>
public sealed class ExportAccountDataHandlerTests
{
    private readonly UserManager<ApplicationUser> _userManager = CreateUserManager();

    [Theory]
    [InlineData("lotrokoniecdev-api")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task Handle_UserIdIsNotAGuid_ReturnsUserNotFound(string userId)
    {
        // Arrange: Identity's own answer to an id it cannot read as a GUID
        _userManager.FindByIdAsync(userId).Throws(new FormatException("Unrecognized Guid format."));

        // Act
        Result<AccountDataExportResponse> result = await CreateSut().Handle(
            new ExportAccountData.Query(userId), CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe("Auth.UserNotFound");
    }

    [Fact]
    public async Task Handle_AccountIsGone_ReturnsUserNotFound()
    {
        // Arrange
        string userId = Guid.NewGuid().ToString();
        _userManager.FindByIdAsync(userId).Returns((ApplicationUser?)null);

        // Act
        Result<AccountDataExportResponse> result = await CreateSut().Handle(
            new ExportAccountData.Query(userId), CancellationToken.None);

        // Assert
        result.Error.Code.ShouldBe("Auth.UserNotFound");
    }

    private ExportAccountData.Handler CreateSut() =>
        new(_userManager, NullLogger<ExportAccountData.Handler>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(),
            null!, null!, null!, null!, null!, null!, null!, null!);
}
