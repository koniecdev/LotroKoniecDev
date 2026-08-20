using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Settings;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IEmailChangeVerificationLinkFactory
{
    string Create(Guid userId, string newEmail, string token);
}

internal interface IEmailChangeRevertLinkFactory
{
    string Create(Guid userId, string previousEmail, string newEmail, string token);
}

/// <summary>
/// Builds the two links of the e-mail change flow. Both take their scheme and host from the
/// configured issuer and never from the request's Host header, so a faked Host cannot redirect a link
/// we send to a mailbox.
/// </summary>
internal sealed class EmailChangeLinkFactory : IEmailChangeVerificationLinkFactory, IEmailChangeRevertLinkFactory
{
    private readonly LinkGenerator _linkGenerator;
    private readonly string _scheme;
    private readonly HostString _host;

    public EmailChangeLinkFactory(
        LinkGenerator linkGenerator,
        IOptions<OpenIddictSettings> openIddictSettings)
    {
        _linkGenerator = linkGenerator;

        Uri issuer = new(openIddictSettings.Value.Issuer, UriKind.Absolute);
        _scheme = issuer.Scheme;
        _host = HostString.FromUriComponent(issuer);
    }

    public string Create(Guid userId, string newEmail, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return Build(
            "/Account/ConfirmEmailChange",
            new { userId = userId.ToString(), email = newEmail, token });
    }

    public string Create(Guid userId, string previousEmail, string newEmail, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(previousEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(newEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return Build(
            "/Account/RevertEmailChange",
            new { userId = userId.ToString(), from = previousEmail, to = newEmail, token });
    }

    private string Build(string page, object values)
    {
        string? link = _linkGenerator.GetUriByPage(
            page: page,
            handler: null,
            values: values,
            scheme: _scheme,
            host: _host);

        return link
            ?? throw new InvalidOperationException(
                $"Could not create the '{page}' link. Ensure the Razor Page is registered.");
    }
}
