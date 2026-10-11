using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// Identity writes its errors in English, and the page must never print that text (#1076). The
/// integration suite covers the rules the real policy refuses. These cases need an answer the real stack
/// gives only in a race or a rare mix, so the store and a validator give it here.
/// </summary>
public sealed class ResetPasswordModelTests
{
    private const string Email = "frodo@shire.me";
    private const string NewPassword = "NewPass99!";
    private const string EnglishDescription = "Optimistic concurrency failure, object has been modified.";
    private const string GeneralMessage = "Upewnij się, że hasło spełnia wymagania, i spróbuj ponownie.";
    private const string TooLongMessage = "Hasło może mieć co najwyżej 128 znaków.";

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new();
    private readonly IUserStore<ApplicationUser> _store = Substitute.For<
        IUserEmailStore<ApplicationUser>,
        IUserPasswordStore<ApplicationUser>>();
    private readonly IUserTwoFactorTokenProvider<ApplicationUser> _tokenProvider =
        Substitute.For<IUserTwoFactorTokenProvider<ApplicationUser>>();
    private readonly IPasswordValidator<ApplicationUser> _passwordValidator =
        Substitute.For<IPasswordValidator<ApplicationUser>>();

    [Theory]
    [InlineData("ConcurrencyFailure")]
    [InlineData("DuplicateEmail")]
    [InlineData("CodeThatIdentityAddsLater")]
    public async Task OnPostAsync_ShouldShowTheGeneralPolishMessage_WhenIdentityRefusesWithACodeThePageDoesNotKnow(
        string code)
    {
        // Arrange
        ArrangeAccountWithAGoodLink();
        _passwordValidator
            .ValidateAsync(Arg.Any<UserManager<ApplicationUser>>(), Arg.Any<ApplicationUser>(), Arg.Any<string?>())
            .Returns(IdentityResult.Success);
        _store
            .UpdateAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>())
            .Returns(IdentityResult.Failed(new IdentityError { Code = code, Description = EnglishDescription }));
        ResetPasswordModel sut = CreatePostingSut();

        // Act
        await PostAsync(sut);

        // Assert
        sut.ErrorMessage.ShouldBe(GeneralMessage);
    }

    [Fact]
    public async Task OnPostAsync_ShouldNameTheRuleAndAddTheGeneralMessageOnce_WhenOtherCodesHaveNoPolishText()
    {
        // Arrange
        ArrangeAccountWithAGoodLink();
        ArrangePasswordRefusal(PasswordMaxLengthValidator.ErrorCode, "FirstUnknownCode", "SecondUnknownCode");
        ResetPasswordModel sut = CreatePostingSut();

        // Act
        await PostAsync(sut);

        // Assert
        sut.ErrorMessage.ShouldBe(TooLongMessage + " " + GeneralMessage);
    }

    /// <summary>
    /// The breach check skips a password that breaks the policy, but the length limit is a separate
    /// validator. So an over-long password can also be found in a breach, and the page names both.
    /// </summary>
    [Fact]
    public async Task OnPostAsync_ShouldNameTheLengthAndTheLeak_WhenAnOverLongPasswordIsAlsoInDataBreaches()
    {
        // Arrange
        ArrangeAccountWithAGoodLink();
        ArrangePasswordRefusal(PasswordMaxLengthValidator.ErrorCode, BreachedPasswordValidator.ErrorCode);
        ResetPasswordModel sut = CreatePostingSut();

        // Act
        await PostAsync(sut);

        // Assert
        sut.ErrorMessage.ShouldBe(TooLongMessage + " To hasło pojawiło się w wyciekach danych, wybierz inne.");
    }

    [Fact]
    public async Task OnPostAsync_ShouldKeepTheForm_WhenIdentityRefusesWithACodeThePageDoesNotKnow()
    {
        // Arrange
        ArrangeAccountWithAGoodLink();
        ArrangePasswordRefusal("CodeThatIdentityAddsLater");
        ResetPasswordModel sut = CreatePostingSut();

        // Act
        await PostAsync(sut);

        // Assert
        sut.TokenInvalid.ShouldBeFalse();
    }

    private void ArrangeAccountWithAGoodLink()
    {
        ((IUserEmailStore<ApplicationUser>)_store)
            .FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationUser { Id = Guid.NewGuid(), Email = Email });
        _tokenProvider
            .ValidateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<UserManager<ApplicationUser>>(),
                Arg.Any<ApplicationUser>())
            .Returns(true);
    }

    private void ArrangePasswordRefusal(params string[] codes) =>
        _passwordValidator
            .ValidateAsync(Arg.Any<UserManager<ApplicationUser>>(), Arg.Any<ApplicationUser>(), Arg.Any<string?>())
            .Returns(IdentityResult.Failed(codes
                .Select(code => new IdentityError { Code = code, Description = EnglishDescription })
                .ToArray()));

    private async Task PostAsync(ResetPasswordModel sut)
    {
        Task<IActionResult> posting = sut.OnPostAsync();
        _clock.Advance(ResponseTimeFloors.AccountLookup);
        await posting.WaitAsync(CompletionTimeout);
    }

    private ResetPasswordModel CreatePostingSut()
    {
        UserManager<ApplicationUser> userManager = new(
            _store,
            Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            [],
            [_passwordValidator],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            services: null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        userManager.RegisterTokenProvider(TokenOptions.DefaultProvider, _tokenProvider);

        return new ResetPasswordModel(
            userManager,
            Substitute.For<IUserSessionRevoker>(),
            new ResponseTimeFloor(_clock),
            NullLogger<ResetPasswordModel>.Instance)
        {
            PageContext = new PageContext { HttpContext = new DefaultHttpContext() },
            Email = Email,
            Token = "mailed-token",
            NewPassword = NewPassword,
            ConfirmPassword = NewPassword
        };
    }
}
