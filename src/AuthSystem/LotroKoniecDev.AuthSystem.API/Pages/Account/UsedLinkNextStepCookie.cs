using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

/// <summary>
/// The undo link and the cancel-deletion link end by sending the visitor on to set a new password. Back from
/// that form leads to the used link's form, and its button would call the link dead (#941). This cookie
/// remembers, in the browser that used the link, where the link sent it, so the used link sends it there
/// again. That place carries a live reset token, so the cookie is encrypted (ADR-0063).
/// </summary>
internal sealed class UsedLinkNextStepCookie
{
    private const string ProtectorPurpose = "LotroKoniecDev.AuthSystem.UsedLinkNextStep.v1";

    private readonly IDataProtector _protector;

    public UsedLinkNextStepCookie(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public void Remember(HttpContext httpContext, UsedLinkFlow flow, string usedToken, PasswordResetStep nextStep)
    {
        string payload = JsonSerializer.Serialize(new Payload(UsedLinkCookie.HashOf(usedToken), nextStep.Email, nextStep.Token));
        httpContext.Response.Cookies.Append(
            NameOf(flow),
            ProtectorFor(flow).Protect(payload),
            UsedLinkCookie.BuildOptions(httpContext.Request.IsHttps));
    }

    public PasswordResetStep? NextStepFor(HttpRequest request, UsedLinkFlow flow, string usedToken)
    {
        if (string.IsNullOrEmpty(usedToken) || !request.Cookies.TryGetValue(NameOf(flow), out string? value))
        {
            return null;
        }

        Payload? payload = Read(ProtectorFor(flow), value);
        if (payload is null || !string.Equals(payload.LinkHash, UsedLinkCookie.HashOf(usedToken), StringComparison.Ordinal))
        {
            return null;
        }

        return new PasswordResetStep(payload.Email, payload.Token);
    }

    public static string NameOf(UsedLinkFlow flow) => flow switch
    {
        UsedLinkFlow.EmailChangeRevert => ".lotrokoniecdev.used-link.email-change-revert",
        UsedLinkFlow.DeletionCancel => ".lotrokoniecdev.used-link.deletion-cancel",
        _ => throw new ArgumentOutOfRangeException(nameof(flow), flow, null)
    };

    /// <summary>
    /// A value that does not decrypt is not ours, or comes from a keyring that is gone. Either way it marks
    /// nothing, and the page shows its form as before.
    /// </summary>
    private static Payload? Read(IDataProtector protector, string value)
    {
        try
        {
            return JsonSerializer.Deserialize<Payload>(protector.Unprotect(value));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// One purpose per flow, so the undo cookie cannot be pasted in as the cancel cookie.
    /// </summary>
    private IDataProtector ProtectorFor(UsedLinkFlow flow) => _protector.CreateProtector(flow.ToString());

    private sealed record Payload(string LinkHash, string Email, string Token);
}

internal enum UsedLinkFlow
{
    EmailChangeRevert,
    DeletionCancel
}

internal sealed record PasswordResetStep(string Email, string Token);
