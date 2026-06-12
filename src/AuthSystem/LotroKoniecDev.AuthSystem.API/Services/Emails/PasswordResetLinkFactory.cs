namespace LotroKoniecDev.AuthSystem.API.Services.Emails;

internal interface IPasswordResetLinkFactory
{
    string Create(string email, string resetToken);
}

internal sealed class PasswordResetLinkFactory : IPasswordResetLinkFactory
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly LinkGenerator _linkGenerator;

    public PasswordResetLinkFactory(
        IHttpContextAccessor httpContextAccessor,
        LinkGenerator linkGenerator)
    {
        _httpContextAccessor = httpContextAccessor;
        _linkGenerator = linkGenerator;
    }

    public string Create(string email, string resetToken)
    {
        if (_httpContextAccessor.HttpContext is null)
        {
            throw new InvalidOperationException("HttpContext is null.");
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email cannot be null or whitespace.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(resetToken))
        {
            throw new ArgumentException("Reset token cannot be null or whitespace.", nameof(resetToken));
        }

        string? resetLink = _linkGenerator.GetUriByPage(
            _httpContextAccessor.HttpContext,
            page: "/Account/ResetPassword",
            values: new { email, token = resetToken });

        return resetLink
            ?? throw new InvalidOperationException(
                "Could not create password reset link. Ensure the '/Account/ResetPassword' page is registered.");
    }
}
