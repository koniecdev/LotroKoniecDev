using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Services.Accounts;
using LotroKoniecDev.AuthSystem.API.Services.Gdpr;
using LotroKoniecDev.AuthSystem.API.Services.RateLimiting;
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
/// <c>returnUrl</c> goes into a hidden field and becomes the redirect target after sign-in, so it is checked
/// on the way in. The class also pins the time floor (ADR-0059) and the answers for a scheduled deletion.
/// </summary>
public sealed class LoginModelTests : IDisposable
{
    private const string Password = "Correct-Horse-1!";
    private const string GeneralMessage = "Nieprawidłowy e-mail lub hasło.";
    private const string DeletionUnderwayMessage = "Usuwanie Twojego konta jest w toku i nie można go już anulować.";

    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Grace = new GdprSettings().DeletionGracePeriod;
    private static readonly TimeSpan RevertLifespan = new EmailChangeRevertTokenProviderOptions().TokenLifespan;
    private static readonly GdprSettings ShortGrace = new() { DeletionGracePeriod = TimeSpan.FromDays(1) };

    private readonly FakeTimeProvider _clock = new();
    private readonly PerAccountFixedWindowThrottle _deletionScheduledLoginThrottle = new(1, TimeSpan.FromMinutes(15));
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
        // Empty credentials return before the store is touched, so the re-rendered page needs no user,
        // and that page puts ReturnUrl back into the form action.
        LoginModel sut = CreateSut();

        await sut.OnPostAsync(returnUrl);

        sut.ReturnUrl.ShouldBe(expected);
    }

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
        sut.ErrorMessage.ShouldBe(GeneralMessage);
    }

    /// <summary>
    /// Only a verified password skips the floor; the unconfirmed answer needs the password (ADR-0046).
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
    /// #881: the lockout never slows guessing on a scheduled deletion, so its own budget does. Once it is
    /// spent, even the right password gets the general message after the floor.
    /// </summary>
    [Fact]
    public async Task OnPostAsync_ShouldShowTheGeneralMessageAfterTheFloor_WhenTheDeletionScheduledLoginBudgetIsSpent()
    {
        // Arrange
        ApplicationUser user = ArrangeDeletionScheduledAccount();
        _ = _deletionScheduledLoginThrottle.TryAcquire(user.Id);
        LoginModel sut = CreatePostingSut(user.Email!, Password);

        // Act
        Task<IActionResult> posting = sut.OnPostAsync();
        bool answeredBeforeTheFloor = posting.IsCompleted;
        _clock.Advance(ResponseTimeFloors.AccountLookup);
        await posting.WaitAsync(CompletionTimeout);

        // Assert
        answeredBeforeTheFloor.ShouldBeFalse();
        sut.ErrorMessage.ShouldBe(GeneralMessage);
    }

    [Fact]
    public async Task OnPostAsync_ShouldNameTheScheduledDeletion_WhileTheDeletionScheduledLoginBudgetLasts()
    {
        // Arrange
        ApplicationUser user = ArrangeDeletionScheduledAccount();
        LoginModel sut = CreatePostingSut(user.Email!, Password);

        // Act
        Task<IActionResult> posting = sut.OnPostAsync();
        bool answeredAtOnce = posting.IsCompletedSuccessfully;
        await posting.WaitAsync(CompletionTimeout);

        // Assert
        answeredAtOnce.ShouldBeTrue();
        sut.ErrorMessage.ShouldNotBeNull();
        sut.ErrorMessage.ShouldStartWith("Twoje konto jest zaplanowane do usunięcia");
    }

    /// <summary>
    /// #916: after its date the account can wait up to a day for the daily run (ADR-0031). The cancel
    /// link is dead by then, so the page names neither the past date nor the link.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(86_399)]
    public async Task OnPostAsync_ShouldSayTheDeletionIsUnderway_OnceTheDeletionDateHasPassed(int secondsPastTheDate)
    {
        // Arrange
        ApplicationUser user = ArrangeDeletionScheduledAccount(
            scheduledAt: _clock.GetUtcNow() - Grace - TimeSpan.FromSeconds(secondsPastTheDate));
        LoginModel sut = CreatePostingSut(user.Email!, Password);

        // Act
        await sut.OnPostAsync().WaitAsync(CompletionTimeout);

        // Assert
        sut.ErrorMessage.ShouldBe(DeletionUnderwayMessage);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3_600)]
    public async Task OnPostAsync_ShouldNameTheDeletionDate_UntilItArrives(int secondsBeforeTheDate)
    {
        // Arrange
        ApplicationUser user = ArrangeDeletionScheduledAccount(
            scheduledAt: _clock.GetUtcNow() - Grace + TimeSpan.FromSeconds(secondsBeforeTheDate));
        LoginModel sut = CreatePostingSut(user.Email!, Password);

        // Act
        await sut.OnPostAsync().WaitAsync(CompletionTimeout);

        // Assert
        sut.ErrorMessage.ShouldNotBeNull();
        sut.ErrorMessage.ShouldStartWith("Twoje konto jest zaplanowane do usunięcia dnia ");
    }

    /// <summary>
    /// The deletion date decides, not the cancel window: with a grace period shorter than the undo window, a
    /// late e-mail change holds the date past the window (#685). There is no undo hint, because the shipped
    /// 14-day settings never get here (#916).
    /// </summary>
    [Fact]
    public async Task OnPostAsync_ShouldNameTheDeletionDate_WhileAnUndoHoldsItPastTheCancelWindow()
    {
        // Arrange
        DateTimeOffset revertArmedAt = _clock.GetUtcNow() - RevertLifespan + TimeSpan.FromHours(1);
        ApplicationUser user = ArrangeDeletionScheduledAccount(
            scheduledAt: revertArmedAt + TimeSpan.FromHours(1), revertArmedAt);
        LoginModel sut = CreatePostingSut(user.Email!, Password, ShortGrace);

        // Act
        await sut.OnPostAsync().WaitAsync(CompletionTimeout);

        // Assert
        sut.ErrorMessage.ShouldNotBeNull();
        sut.ErrorMessage.ShouldStartWith("Twoje konto jest zaplanowane do usunięcia dnia ");
    }

    [Fact]
    public async Task OnPostAsync_ShouldSayTheDeletionIsUnderway_OnceAnUndoThatHeldTheDateHasExpired()
    {
        // Arrange
        DateTimeOffset revertArmedAt = _clock.GetUtcNow() - RevertLifespan - TimeSpan.FromSeconds(1);
        ApplicationUser user = ArrangeDeletionScheduledAccount(
            scheduledAt: revertArmedAt + TimeSpan.FromHours(1), revertArmedAt);
        LoginModel sut = CreatePostingSut(user.Email!, Password, ShortGrace);

        // Act
        await sut.OnPostAsync().WaitAsync(CompletionTimeout);

        // Assert
        sut.ErrorMessage.ShouldBe(DeletionUnderwayMessage);
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
    /// This host's root serves the discovery JSON, a dead end for a browser, so the fallback is the frontend's
    /// login route. <c>FrontendUrlTests</c> pins how it is built.
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

    public void Dispose()
    {
        _deletionScheduledLoginThrottle.Dispose();
    }

    private ApplicationUser ArrangeDeletionScheduledAccount(
        DateTimeOffset? scheduledAt = null, DateTimeOffset? revertArmedAt = null)
    {
        ApplicationUser user = new()
        {
            Id = Guid.NewGuid(),
            Email = "frodo@shire.me",
            PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Password),
            DeletionScheduledAt = scheduledAt ?? _clock.GetUtcNow(),
            EmailChangeRevertArmedAt = revertArmedAt
        };
        ((IUserEmailStore<ApplicationUser>)_store)
            .FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(user);
        ((IUserPasswordStore<ApplicationUser>)_store)
            .GetPasswordHashAsync(user, Arg.Any<CancellationToken>())
            .Returns(user.PasswordHash);
        return user;
    }

    private LoginModel CreateSut(params string[] postLogoutRedirectUris) =>
        CreateSut(new GdprSettings(), postLogoutRedirectUris);

    private LoginModel CreateSut(GdprSettings gdprSettings, params string[] postLogoutRedirectUris) =>
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
                Microsoft.Extensions.Options.Options.Create(gdprSettings)),
            _deletionScheduledLoginThrottle,
            new ResponseTimeFloor(_clock),
            _clock,
            NullLogger<LoginModel>.Instance);

    private LoginModel CreatePostingSut(string email, string password, GdprSettings? gdprSettings = null)
    {
        LoginModel sut = CreateSut(gdprSettings ?? new GdprSettings());
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
