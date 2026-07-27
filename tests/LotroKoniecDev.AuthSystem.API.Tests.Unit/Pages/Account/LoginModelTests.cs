using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// The login page both reflects <c>returnUrl</c> into its own form action and uses it as the
/// post-sign-in redirect target, so the value has to be sanitized on the way in — a raw query value
/// must never reach the property. Pins the wiring the register page already had.
/// </summary>
public sealed class LoginModelTests
{
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
        // The empty-credentials branch returns before any store call, so the re-rendered page is
        // observable without a user — and that page carries ReturnUrl back into the form action.
        LoginModel sut = CreateSut();

        await sut.OnPostAsync(returnUrl);

        sut.ReturnUrl.ShouldBe(expected);
    }

    /// <summary>
    /// The sign-in falls back to this URL when it carries no local continuation, so it must point at
    /// the frontend's own login route — this host's root serves the API discovery JSON and dead-ends a
    /// browser arriving from the reset-password or confirm-email pages. The URL-building rules
    /// themselves are pinned by <c>FrontendUrlTests</c>.
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

    private static LoginModel CreateSut(params string[] postLogoutRedirectUris) =>
        new(
            CreateUserManager(),
            Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings
            {
                Issuer = "https://auth.localhost",
                WebClient = new WebClientSettings { PostLogoutRedirectUris = postLogoutRedirectUris }
            }),
            Microsoft.Extensions.Options.Options.Create(new GdprSettings()),
            NullLogger<LoginModel>.Instance);

    private static UserManager<ApplicationUser> CreateUserManager() =>
        new(
            Substitute.For<IUserStore<ApplicationUser>>(),
            Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(),
            [],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            services: null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
}
