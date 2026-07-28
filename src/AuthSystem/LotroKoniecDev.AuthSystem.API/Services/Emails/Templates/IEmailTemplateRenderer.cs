using LotroKoniecDev.AuthSystem.Infrastructure.Emails;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

internal interface IEmailTemplateRenderer
{
    EmailBody Render(EmailTemplateModel model);
}
