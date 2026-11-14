using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.DbContexts;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Accounts;

/// <summary>
/// The reservation answers "reserved" for every address here, so a success proves the validator never asked.
/// The real normalizer is used: a substituted one returns null on both sides and every comparison passes
/// blindly (ADR-0048).
/// </summary>
public sealed class EmailChangeRevertReservationValidatorTests
{
    private const string StoredEmail = "frodo@shire.me";
    private const string ReservedEmail = "frodo@rivendell.me";

    private readonly IEmailChangeRevertReservation _revertReservation = Substitute.For<IEmailChangeRevertReservation>();
    private readonly AuthDbContext _db = CreateDetachedDbContext();
    private readonly HttpContextAccessor _httpContextAccessor = new();
    private readonly UserManager<ApplicationUser> _userManager =
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);

    [Fact]
    public async Task ValidateAsync_NewAccountOnAReservedAddress_FailsWithTheReservationCode()
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = CreateUser(ReservedEmail);

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(EmailChangeRevertReservationValidator.ErrorCode);
    }

    /// <summary>
    /// A caller that forgets to map the code shows the description, so it must not say more than a taken
    /// address does.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_RefusedAddress_DescriptionNeverNamesTheAddress()
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = CreateUser(ReservedEmail);

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Errors.ShouldHaveSingleItem().Description.ShouldNotContain(ReservedEmail, Case.Insensitive);
    }

    [Fact]
    public async Task ValidateAsync_AddressIsFree_Succeeds()
    {
        // Arrange
        _revertReservation.IsReservedAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        ApplicationUser user = CreateUser(ReservedEmail);

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_AddedAccountOnAReservedAddress_Fails()
    {
        // Arrange: an Added entry reports its current value as the original, so it must still count as new
        StubEveryAddressAsReserved();
        ApplicationUser user = CreateUser(ReservedEmail);
        _db.Users.Add(user);

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_LoadedAccountMovedOntoAReservedAddress_Fails()
    {
        // Arrange: no change detection ran, as in UserManager.UpdateAsync
        StubEveryAddressAsReserved();
        ApplicationUser user = AttachStoredUser();
        user.Email = ReservedEmail;

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_ModifiedAccountMovedOntoAReservedAddress_Fails()
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = AttachStoredUser();
        user.Email = ReservedEmail;
        _db.ChangeTracker.DetectChanges();

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    /// <summary>
    /// A failed login, a lockout or a password change: the address another row holds is the account's own,
    /// so the save must go through without a query (#905).
    /// </summary>
    [Fact]
    public async Task ValidateAsync_LoadedAccountKeepsItsAddress_SucceedsWithoutAsking()
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = AttachStoredUser();
        user.AccessFailedCount++;

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_ModifiedAccountKeepsItsAddress_SucceedsWithoutAsking()
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = AttachStoredUser();
        user.AccessFailedCount++;
        _db.ChangeTracker.DetectChanges();

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("FRODO@SHIRE.ME")]
    [InlineData("Frodo@Shire.Me")]
    public async Task ValidateAsync_LoadedAccountRespellsItsOwnAddress_SucceedsWithoutAsking(string respelled)
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = AttachStoredUser();
        user.Email = respelled;

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ValidateAsync_BlankAddress_LeavesItToTheBuiltInValidator(string? email)
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = CreateUser(email);

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// Moving back to its own armed address is what the reservation protects, so the account's own row
    /// must never block it.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_ExcludesTheAccountItself()
    {
        // Arrange: only a query that excludes this account answers "free"
        ApplicationUser user = AttachStoredUser();
        _revertReservation.IsReservedAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _revertReservation.IsReservedAsync(ReservedEmail, user.Id, Arg.Any<CancellationToken>())
            .Returns(false);
        user.Email = ReservedEmail;

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_DuringARequest_AsksWithTheRequestsAbortToken()
    {
        // Arrange: only a query made with the request's own token sees the reservation
        using CancellationTokenSource requestAborted = new();
        _httpContextAccessor.HttpContext = new DefaultHttpContext { RequestAborted = requestAborted.Token };
        _revertReservation.IsReservedAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _revertReservation.IsReservedAsync(Arg.Any<string>(), Arg.Any<Guid?>(), requestAborted.Token)
            .Returns(true);
        ApplicationUser user = CreateUser(ReservedEmail);

        // Act
        IdentityResult result = await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    /// <summary>
    /// The validator switches change detection off to read the entry. Left off, every later save in the
    /// request would miss its changes, and nothing in the result would show it.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_LeavesChangeDetectionOnAsItFoundIt()
    {
        // Arrange
        StubEveryAddressAsReserved();
        ApplicationUser user = AttachStoredUser();
        user.Email = ReservedEmail;

        // Act
        await CreateValidator().ValidateAsync(_userManager, user);

        // Assert
        _db.ChangeTracker.AutoDetectChangesEnabled.ShouldBeTrue();
    }

    private void StubEveryAddressAsReserved() =>
        _revertReservation.IsReservedAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(true);

    /// <summary>The state an account is in after a lookup through <c>UserManager</c>: tracked and unchanged.</summary>
    private ApplicationUser AttachStoredUser()
    {
        ApplicationUser user = CreateUser(StoredEmail);
        _db.Users.Attach(user);
        return user;
    }

    private EmailChangeRevertReservationValidator CreateValidator() =>
        new(_revertReservation, _db, new UpperInvariantLookupNormalizer(), _httpContextAccessor);

    private static ApplicationUser CreateUser(string? email) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserName = "frodo",
            Email = email,
            SecurityStamp = Guid.NewGuid().ToString()
        };

    /// <summary>The validator only reads the change tracker, which needs no connection.</summary>
    private static AuthDbContext CreateDetachedDbContext() =>
        new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=unit-test;Database=none;Username=none;Password=none")
            .Options);
}
