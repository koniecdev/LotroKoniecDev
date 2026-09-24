using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.ResponseTiming;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using LotroKoniecDev.AuthSystem.Persistence.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// The login page puts <c>returnUrl</c> into a hidden field and also uses it as the redirect target
/// after sign-in, so the value has to be checked on the way in and a raw query value must never reach
/// the property. This pins the same wiring the register page already had.
/// </summary>
public sealed class LoginModelTests
{
    private const string Password = "Correct-Horse-1!";

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);

    private readonly FakeTimeProvider _clock = new();
    private readonly IUserStore<ApplicationUser> _store = Substitute.For<
        IUserEmailStore<ApplicationUser>,
        IUserPasswordStore<ApplicationUser>,
        IUserLockoutStore<ApplicationUser>>();

    [Theory]
    [InlineData("/connect/authorize?client_id=web", "/connect/authorize?client_id=web")]
    [InlineData("/", "/")]
    [InlineData("https://evil.example/harvest", null)]
    [InlineData("//evil.example", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("/\t/evil.example", null)]
    [InlineData(null, null)]
    public void OnGet_KeepsOnlyALocalReturnUrl(string? returnUrl, string? expected)
    {
        LoginModel sut = CreateSut();

        sut.OnGet(returnUrl);

        sut.ReturnUrl.ShouldBe(expected);
    }

    [Theory]
    [InlineData("/connect/authorize?client_id=web", "/connect/authorize?client_id=web")]
    [InlineData("https://evil.example/harvest", null)]
    [InlineData("/\t/evil.example", null)]
    public async Task OnPostAsync_KeepsOnlyALocalReturnUrl_EvenWhenTheCredentialsAreRejected(
        string returnUrl, string? expected)
    {
        // With empty credentials the page returns before it touches the store, so we can see the
        // re-rendered page without creating a user, and that page puts ReturnUrl back into the form
        // action.
        LoginModel sut = CreateSut();

        await sut.OnPostAsync(returnUrl);

        sut.ReturnUrl.ShouldBe(expected);
    }

    /// <summary>
    /// ADR-0059: an answer that shows the general message leaves no sooner than the floor.
    /// </summary>
    [Fact]
    public async Task OnPostAsync_ShouldNotAnswerBeforeTheFloor_WhenTheAddressHasNoAccount()
    {
        // Arrange
        ((IUserEmailStore<ApplicationUser>)_store)
            .FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ApplicationUser?)null);
        LoginModel sut = CreatePostingSut("nobody@example.com", Password);

        // Act
        Task<IActionResult> posting = sut.OnPostAsync();
        bool answeredBeforeTheFloor = posting.IsCompleted;
        _clock.Advance(ResponseTimeFloors.AccountLookup);
        await posting.WaitAsync(CompletionTimeout);

        // Assert
        answeredBeforeTheFloor.ShouldBeFalse();
        sut.ErrorMessage.ShouldBe("Nieprawidłowy e-mail lub hasło.");
    }

    /// <summary>
    /// Only an account whose password was verified skips the floor. The unconfirmed answer needs the
    /// password (ADR-0046), so it leaves at once, without the clock moving.
    /// </summary>
    [Fact]
    public async Task OnPostAsync_ShouldAnswerWithoutWaiting_WhenThePasswordIsVerified()
    {
        // Arrange
        ApplicationUser user = new()
        {
            Email = "frodo@shire.me",
            PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Password)
        };
        ((IUserEmailStore<ApplicationUser>)_store)
            .FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(user);
        ((IUserPasswordStore<ApplicationUser>)_store)
            .GetPasswordHashAsync(user, Arg.Any<CancellationToken>())
            .Returns(user.PasswordHash);
        LoginModel sut = CreatePostingSut(user.Email, Password);

        // Act
        Task<IActionResult> posting = sut.OnPostAsync();
        bool answeredAtOnce = posting.IsCompletedSuccessfully;
        await posting.WaitAsync(CompletionTimeout);

        // Assert
        answeredAtOnce.ShouldBeTrue();
        sut.ResendConfirmationEmail.ShouldBe(user.Email);
    }

    /// <summary>
    /// The wait sits in a finally, so a lookup that throws does not answer early either.
    /// </summary>
    [Fact]
    public async Task OnPostAsync_ShouldStillWaitForTheFloor_WhenTheLookupThrows()
    {
        // Arrange
        ((IUserEmailStore<ApplicationUser>)_store)
            .FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ApplicationUser?>(new InvalidOperationException("The database is down.")));
        LoginModel sut = CreatePostingSut("frodo@shire.me", Password);

        // Act
        Task<IActionResult> posting = sut.OnPostAsync();
        bool answeredBeforeTheFloor = posting.IsCompleted;
        _clock.Advance(ResponseTimeFloors.AccountLookup);

        // Assert
        answeredBeforeTheFloor.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(() => posting.WaitAsync(CompletionTimeout));
    }

    /// <summary>
    /// The sign-in falls back to this URL when it has nowhere else to continue, so it has to point at the
    /// frontend's own login route. This host's root serves the API discovery JSON, which is a dead end
    /// for a browser coming from the reset-password or confirm-email pages.
    /// How the URL is built is pinned by <c>FrontendUrlTests</c>.
    /// </summary>
    [Fact]
    public void FrontendLoginUrl_PointsAtTheFrontendLoginRoute()
    {
        LoginModel sut = CreateSut("https://lotro-translator.pl");

        sut.FrontendLoginUrl.ShouldBe("https://lotro-translator.pl/auth/login");
    }

    [Fact]
    public void FrontendLoginUrl_IsNull_WhenTheWebClientHasNoConfiguredUri()
    {
        LoginModel sut = CreateSut();

        sut.FrontendLoginUrl.ShouldBeNull();
    }

    private LoginModel CreateSut(params string[] postLogoutRedirectUris) =>
        new(
            CreateUserManager(_store),
            Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings
            {
                Issuer = "https://auth.localhost",
                WebClient = new WebClientSettings { PostLogoutRedirectUris = postLogoutRedirectUris }
            }),
            new AccountDeletionSchedule(
                new EmailChangeRevertWindow(
                    Microsoft.Extensions.Options.Options.Create(new EmailChangeRevertTokenProviderOptions())),
                Microsoft.Extensions.Options.Options.Create(new GdprSettings())),
            new ResponseTimeFloor(_clock),
            NullLogger<LoginModel>.Instance);

    private LoginModel CreatePostingSut(string email, string password)
    {
        LoginModel sut = CreateSut();
        sut.PageContext = new PageContext { HttpContext = new DefaultHttpContext() };
        sut.Email = email;
        sut.Password = password;
        return sut;
    }

    private static UserManager<ApplicationUser> CreateUserManager(IUserStore<ApplicationUser> store) =>
        new(
            store,
            Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            services: null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
}
