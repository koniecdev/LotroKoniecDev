using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Services.Sessions;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
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
/// integration suite covers the rules the real policy refuses. A refusal from the save comes only in a
/// race, so the store gives it here.
/// </summary>
public sealed class ResetPasswordModelTests
{
    private const string Email = "frodo@shire.me";
    private const string NewPassword = "NewPass99!";
    private const string GeneralMessage = "Spróbuj jeszcze raz. Jeśli błąd się powtórzy, napisz do nas.";

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new();
    private readonly IUserStore<ApplicationUser> _store = Substitute.For<
        IUserEmailStore<ApplicationUser>,
        IUserPasswordStore<ApplicationUser>>();
    private readonly IUserTwoFactorTokenProvider<ApplicationUser> _tokenProvider =
        Substitute.For<IUserTwoFactorTokenProvider<ApplicationUser>>();

    [Theory]
    [InlineData("ConcurrencyFailure")]
    [InlineData("DuplicateEmail")]
    [InlineData("CodeThatIdentityAddsLater")]
    public async Task OnPostAsync_ShouldShowTheGeneralPolishMessage_WhenIdentityRefusesWithACodeThePageDoesNotKnow(
        string code)
    {
        // Arrange
        ArrangeAccountWithAGoodLink();
        ArrangeSaveRefusal(code);
        ResetPasswordModel sut = CreatePostingSut();

        // Act
        await PostAsync(sut);

        // Assert
        sut.ErrorMessage.ShouldBe(GeneralMessage);
    }

    [Fact]
    public async Task OnPostAsync_ShouldShowTheGeneralMessageOnce_WhenSeveralCodesHaveNoPolishText()
    {
        // Arrange
        ArrangeAccountWithAGoodLink();
        ArrangeSaveRefusal("InvalidUserName", "DuplicateEmail");
        ResetPasswordModel sut = CreatePostingSut();

        // Act
        await PostAsync(sut);

        // Assert
        sut.ErrorMessage.ShouldBe(GeneralMessage);
    }

    /// <summary>
    /// The general message asks for another try, so the form has to stay for it.
    /// </summary>
    [Fact]
    public async Task OnPostAsync_ShouldKeepTheForm_WhenIdentityRefusesWithACodeThePageDoesNotKnow()
    {
        // Arrange
        ArrangeAccountWithAGoodLink();
        ArrangeSaveRefusal("ConcurrencyFailure");
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

    private void ArrangeSaveRefusal(params string[] codes) =>
        _store
            .UpdateAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>())
            .Returns(IdentityResult.Failed(codes
                .Select(code => new IdentityError
                {
                    Code = code,
                    Description = "Optimistic concurrency failure, object has been modified."
                })
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
            [],
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
