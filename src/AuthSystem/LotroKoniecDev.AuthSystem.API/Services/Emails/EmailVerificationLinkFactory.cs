namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IEmailVerificationLinkFactory
{
    string Create(string email, string emailVerificationToken);
}

internal sealed class EmailVerificationLinkFactory : IEmailVerificationLinkFactory
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly LinkGenerator _linkGenerator;

    public EmailVerificationLinkFactory(
        IHttpContextAccessor httpContextAccessor,
        LinkGenerator linkGenerator)
    {
        _httpContextAccessor = httpContextAccessor;
        _linkGenerator = linkGenerator;
    }

    public string Create(string email, string emailVerificationToken)
    {
        if (_httpContextAccessor.HttpContext is null)
        {
            throw new InvalidOperationException("HttpContext is null.");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email cannot be null or whitespace.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(emailVerificationToken))
        {
            throw new ArgumentException("Email verification token cannot be null or whitespace.", nameof(emailVerificationToken));
        }

        string? verificationLink = _linkGenerator.GetUriByPage(
            _httpContextAccessor.HttpContext,
            page: "/Account/ConfirmEmail",
            handler: null,
            values: new { email, token = emailVerificationToken });

        return verificationLink
            ?? throw new InvalidOperationException(
                "Could not create verification link. Ensure the '/Account/ConfirmEmail' Razor Page is registered.");
    }
}
