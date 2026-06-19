using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IPasswordResetLinkFactory
{
    string Create(string email, string resetToken);
}

internal sealed class PasswordResetLinkFactory : IPasswordResetLinkFactory
{
    private readonly LinkGenerator _linkGenerator;
    private readonly string _scheme;
    private readonly HostString _host;

    public PasswordResetLinkFactory(
        LinkGenerator linkGenerator,
        IOptions<OpenIddictSettings> openIddictSettings)
    {
        _linkGenerator = linkGenerator;

        // Scheme + host come from the configured issuer, never the request Host header, so a
        // forged Host cannot poison the reset link that gets emailed to the account owner.
        Uri issuer = new(openIddictSettings.Value.Issuer, UriKind.Absolute);
        _scheme = issuer.Scheme;
        _host = HostString.FromUriComponent(issuer);
    }

    public string Create(string email, string resetToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email cannot be null or whitespace.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(resetToken))
        {
            throw new ArgumentException("Reset token cannot be null or whitespace.", nameof(resetToken));
        }

        string? resetLink = _linkGenerator.GetUriByPage(
            page: "/Account/ResetPassword",
            handler: null,
            values: new { email, token = resetToken },
            scheme: _scheme,
            host: _host);

        return resetLink
            ?? throw new InvalidOperationException(
                "Could not create password reset link. Ensure the '/Account/ResetPassword' page is registered.");
    }
}
