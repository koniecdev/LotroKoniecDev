using LotroKoniecDev.AuthSystem.API.Pages.Account;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Domain.Aggregates.ApplicationUsers.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Pages.Account;

/// <summary>
/// The login page puts <c>returnUrl</c> into its own form action and also uses it as the redirect target
/// after sign-in, so the value has to be checked on the way in and a raw query value must never reach
/// the property. This pins the same wiring the register page already had.
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
        // With empty credentials the page returns before it touches the store, so we can see the
        // re-rendered page without creating a user, and that page puts ReturnUrl back into the form
        // action.
        LoginModel sut = CreateSut();

        await sut.OnPostAsync(returnUrl);

        sut.ReturnUrl.ShouldBe(expected);
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
