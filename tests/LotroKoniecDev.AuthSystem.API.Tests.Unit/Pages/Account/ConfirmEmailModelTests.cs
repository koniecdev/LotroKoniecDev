using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// Identity writes its errors in English, and the page must never print that text (#1076). Identity
/// refuses a good link only in a race, such as a second click on the link while the first one saves, so
/// the store gives that answer here.
/// </summary>
public sealed class ConfirmEmailModelTests
{
    private const string Email = "frodo@shire.me";
    private const string InvalidLinkTitle = "Link wygasł lub jest nieprawidłowy";

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new();
    private readonly IUserEmailStore<ApplicationUser> _store = Substitute.For<IUserEmailStore<ApplicationUser>>();
    private readonly IUserTwoFactorTokenProvider<ApplicationUser> _tokenProvider =
        Substitute.For<IUserTwoFactorTokenProvider<ApplicationUser>>();

    [Theory]
    [InlineData("ConcurrencyFailure")]
    [InlineData("DuplicateEmail")]
    [InlineData("CodeThatIdentityAddsLater")]
    public async Task OnGet_ShouldSayInPolishThatTheConfirmationFailed_WhenIdentityRefusesAGoodLink(string code)
    {
        // Arrange
        ArrangeUnconfirmedAccount(linkVerifies: true);
        _store
            .UpdateAsync(Arg.Any<ApplicationUser>(), Arg.Any<CancellationToken>())
            .Returns(IdentityResult.Failed(new IdentityError
            {
                Code = code,
                Description = "Optimistic concurrency failure, object has been modified."
            }));
        ConfirmEmailModel sut = CreateSut();

        // Act
        await GetAsync(sut);

        // Assert
        sut.ErrorTitle.ShouldBe("Nie udało się potwierdzić adresu");
        sut.ErrorMessage.ShouldBe("Otwórz link z wiadomości jeszcze raz. Jeśli błąd się powtórzy, napisz do nas.");
    }

    [Fact]
    public async Task OnGet_ShouldCallTheLinkDead_WhenTheLinkDoesNotVerify()
    {
        // Arrange
        ArrangeUnconfirmedAccount(linkVerifies: false);
        ConfirmEmailModel sut = CreateSut();

        // Act
        await GetAsync(sut);

        // Assert
        sut.ErrorTitle.ShouldBe(InvalidLinkTitle);
        sut.ErrorMessage.ShouldBe(
            "Link potwierdzający jest nieprawidłowy lub wygasł. Jeśli Twoje konto jest już aktywne, po prostu się zaloguj.");
    }

    [Fact]
    public async Task OnGet_ShouldCallTheLinkDead_WhenTheAddressHasNoAccount()
    {
        // Arrange
        _store
            .FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ApplicationUser?)null);
        ConfirmEmailModel sut = CreateSut();

        // Act
        await GetAsync(sut);

        // Assert
        sut.ErrorTitle.ShouldBe(InvalidLinkTitle);
    }

    private void ArrangeUnconfirmedAccount(bool linkVerifies)
    {
        _store
            .FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ApplicationUser { Id = Guid.NewGuid(), Email = Email });
        _tokenProvider
            .ValidateAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<UserManager<ApplicationUser>>(),
                Arg.Any<ApplicationUser>())
            .Returns(linkVerifies);
    }

    private async Task GetAsync(ConfirmEmailModel sut)
    {
        Task getting = sut.OnGet(Email, "mailed-token");
        _clock.Advance(ResponseTimeFloors.AccountLookup);
        await getting.WaitAsync(CompletionTimeout);
    }

    private ConfirmEmailModel CreateSut()
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

        return new ConfirmEmailModel(userManager, new ResponseTimeFloor(_clock), NullLogger<ConfirmEmailModel>.Instance);
    }
}
