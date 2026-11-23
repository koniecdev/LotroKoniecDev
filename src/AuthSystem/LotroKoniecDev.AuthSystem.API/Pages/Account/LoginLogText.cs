namespace LotroKoniecDev.AuthSystem.API.Pages.Account;

/// <summary>
/// The Grafana rule <c>lotro-unconfirmed-sign-in</c> on the box matches this prefix (#1510). CD does not
/// carry the rule file, so a change here also needs the <c>/opt/obs</c> sync (runbook, Observability).
/// </summary>
internal static class LoginLogText
{
    public const string EmailNotConfirmedPrefix = "Failed login: email not confirmed.";

    public const string EmailNotConfirmedTemplate = EmailNotConfirmedPrefix + " UserId: {UserId}, IP: {IP}";
}
