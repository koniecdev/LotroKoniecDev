using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using NSubstitute;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Tests.Unit.Shared;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Infrastructure.PwnedPasswords;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using LotroKoniecDev.SharedKernel.Constants;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Accounts;

public sealed class BreachedPasswordValidatorTests
{
    private const string Password = "Password1!";

    private readonly IPwnedPasswordChecker _pwnedPasswordChecker = Substitute.For<IPwnedPasswordChecker>();
    private readonly UserManager<ApplicationUser> _userManager =
        Substitute.For<UserManager<ApplicationUser>>(
            Substitute.For<IUserStore<ApplicationUser>>(), null, null, null, null, null, null, null, null);
    private readonly HttpContextAccessor _httpContextAccessor = new();
    private readonly CapturingLogger<BreachedPasswordValidator> _logger = new();

    [Fact]
    public async Task ValidateAsync_WhenThePasswordIsInABreach_FailsWithTheBreachCode()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(BreachedPasswordValidator.ErrorCode);
    }

    /// <summary>
    /// Without this line an operator cannot tell a check that refuses passwords from one that never runs.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenThePasswordIsInABreach_LogsTheRefusal()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        _logger.Entries.ShouldHaveSingleItem().EventId.ShouldBe(EventIds.PasswordRefusedAsBreached);
    }

    [Fact]
    public async Task ValidateAsync_DuringARequest_ChecksWithTheRequestsAbortToken()
    {
        // Arrange: only a check made with the request's own token sees the breach
        using CancellationTokenSource requestAborted = new();
        _httpContextAccessor.HttpContext = new DefaultHttpContext { RequestAborted = requestAborted.Token };
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.NotFound);
        _pwnedPasswordChecker.CheckAsync(Password, requestAborted.Token)
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateAsync_WhenThePasswordIsInNoKnownBreach_Succeeds()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.NotFound);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// The failure policy of ADR-0066: an unknown answer lets the password through instead of blocking
    /// registration, password change and reset while the service is down.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenTheServiceIsUnavailable_SucceedsSoTheFormStillWorks()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Unavailable);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    /// <summary>
    /// The built-in validator refuses these with an error that names the rule, so this one stays out of
    /// the way instead of adding the vaguer breach message on top.
    /// </summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("password")]
    [InlineData("PASSWORD1!")]
    [InlineData("Password1")]
    public async Task ValidateAsync_WhenThePasswordBreaksThePolicy_SucceedsAndLeavesItToTheBuiltInRules(string password)
    {
        // Arrange: a checker that calls everything breached
        _pwnedPasswordChecker.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), password);

        // Assert
        result.Succeeded.ShouldBeTrue();
        await _pwnedPasswordChecker.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The length limit is a validator of its own, not part of the built-in policy. A password it refuses
    /// must not reach the outside service either, and must not get the breach message on top (#1110).
    /// </summary>
    [Theory]
    [InlineData(PasswordConstants.MaxLength + 1)]
    [InlineData(500)]
    public async Task ValidateAsync_WhenThePasswordIsLongerThanTheMaximum_SucceedsWithoutAskingTheService(int length)
    {
        // Arrange: a password that passes the built-in policy, and a checker that calls everything breached
        _pwnedPasswordChecker.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), PasswordOfLength(length));

        // Assert
        result.Succeeded.ShouldBeTrue();
        await _pwnedPasswordChecker.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ValidateAsync_WhenAPasswordOfExactlyTheMaximumLengthIsInABreach_FailsWithTheBreachCode()
    {
        // Arrange
        string password = PasswordOfLength(PasswordConstants.MaxLength);
        _pwnedPasswordChecker.CheckAsync(password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), password);

        // Assert
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(BreachedPasswordValidator.ErrorCode);
    }

    /// <summary>
    /// Every other password validator counts, so a rule added later needs no change here.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenAnyOtherPasswordRuleRefusesThePassword_SucceedsWithoutAskingTheService()
    {
        // Arrange: a password every current rule accepts, a rule that refuses it, and a checker that calls
        // everything breached
        IPasswordValidator<ApplicationUser> otherRule = Substitute.For<IPasswordValidator<ApplicationUser>>();
        otherRule.ValidateAsync(Arg.Any<UserManager<ApplicationUser>>(), Arg.Any<ApplicationUser>(), Arg.Any<string?>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = "AnotherPasswordRule" }));
        _pwnedPasswordChecker.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator(otherRule);

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Succeeded.ShouldBeTrue();
        await _pwnedPasswordChecker.DidNotReceive().CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// If the check skipped only this same instance, the second copy would call this one back, and the
    /// test host would end in a stack overflow.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_WhenASecondCopyOfTheValidatorIsRegistered_SkipsItAndStillRefusesABreachedPassword()
    {
        // Arrange
        _pwnedPasswordChecker.CheckAsync(Password, Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator(
            new BreachedPasswordValidator(_pwnedPasswordChecker, _httpContextAccessor, _logger));

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), Password);

        // Assert
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(BreachedPasswordValidator.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ValidateAsync_WhenThereIsNoPassword_SucceedsAndLeavesItToTheBuiltInRules(string? password)
    {
        // Arrange: even a checker that calls everything breached must not be what refuses an empty password
        _pwnedPasswordChecker.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PwnedPasswordVerdict.Breached);
        BreachedPasswordValidator validator = CreateValidator();

        // Act
        IdentityResult result = await validator.ValidateAsync(_userManager, new ApplicationUser(), password);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    private static string PasswordOfLength(int length) => "Aa1!" + new string('x', length - 4);

    /// <summary>
    /// The manager holds the validators of the real registration, this one included, so every test also
    /// shows that the validator does not call itself.
    /// </summary>
    private BreachedPasswordValidator CreateValidator(params IPasswordValidator<ApplicationUser>[] otherRules)
    {
        BreachedPasswordValidator validator = new(_pwnedPasswordChecker, _httpContextAccessor, _logger);
        _userManager.PasswordValidators.Add(new PasswordValidator<ApplicationUser>());
        _userManager.PasswordValidators.Add(new PasswordMaxLengthValidator());
        foreach (IPasswordValidator<ApplicationUser> otherRule in otherRules)
        {
            _userManager.PasswordValidators.Add(otherRule);
        }

        _userManager.PasswordValidators.Add(validator);

        return validator;
    }
}
