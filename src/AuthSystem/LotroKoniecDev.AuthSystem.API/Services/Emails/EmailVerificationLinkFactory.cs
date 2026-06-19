using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IEmailVerificationLinkFactory
{
    string Create(string email, string emailVerificationToken);
}

internal sealed class EmailVerificationLinkFactory : IEmailVerificationLinkFactory
{
    private readonly LinkGenerator _linkGenerator;
    private readonly string _scheme;
    private readonly HostString _host;

    public EmailVerificationLinkFactory(
        LinkGenerator linkGenerator,
        IOptions<OpenIddictSettings> openIddictSettings)
    {
        _linkGenerator = linkGenerator;

        // Scheme + host come from the configured issuer, never the request Host header, so a
        // forged Host cannot poison the confirmation link that gets emailed to the account owner.
        Uri issuer = new(openIddictSettings.Value.Issuer, UriKind.Absolute);
        _scheme = issuer.Scheme;
        _host = HostString.FromUriComponent(issuer);
    }

    public string Create(string email, string emailVerificationToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email cannot be null or whitespace.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(emailVerificationToken))
        {
            throw new ArgumentException("Email verification token cannot be null or whitespace.", nameof(emailVerificationToken));
        }

        string? verificationLink = _linkGenerator.GetUriByPage(
            page: "/Account/ConfirmEmail",
            handler: null,
            values: new { email, token = emailVerificationToken },
            scheme: _scheme,
            host: _host);

        return verificationLink
            ?? throw new InvalidOperationException(
                "Could not create verification link. Ensure the '/Account/ConfirmEmail' Razor Page is registered.");
    }
}
