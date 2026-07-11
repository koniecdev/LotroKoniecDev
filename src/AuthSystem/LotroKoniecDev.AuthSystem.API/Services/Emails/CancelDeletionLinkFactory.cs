using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface ICancelDeletionLinkFactory
{
    string Create(string email, string cancelToken);
}

internal sealed class CancelDeletionLinkFactory : ICancelDeletionLinkFactory
{
    private readonly LinkGenerator _linkGenerator;
    private readonly string _scheme;
    private readonly HostString _host;

    public CancelDeletionLinkFactory(
        LinkGenerator linkGenerator,
        IOptions<OpenIddictSettings> openIddictSettings)
    {
        _linkGenerator = linkGenerator;

        // Scheme + host come from the configured issuer, never the request Host header, so a
        // forged Host cannot poison the cancel link that gets emailed to the account owner.
        Uri issuer = new(openIddictSettings.Value.Issuer, UriKind.Absolute);
        _scheme = issuer.Scheme;
        _host = HostString.FromUriComponent(issuer);
    }

    public string Create(string email, string cancelToken)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email cannot be null or whitespace.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(cancelToken))
        {
            throw new ArgumentException("Cancel token cannot be null or whitespace.", nameof(cancelToken));
        }

        string? cancelLink = _linkGenerator.GetUriByPage(
            page: "/Account/CancelDeletion",
            handler: null,
            values: new { email, token = cancelToken },
            scheme: _scheme,
            host: _host);

        return cancelLink
            ?? throw new InvalidOperationException(
                "Could not create cancel-deletion link. Ensure the '/Account/CancelDeletion' page is registered.");
    }
}
