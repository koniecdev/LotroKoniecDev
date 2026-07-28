using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using LotroKoniecDev.AuthSystem.API.Common;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;

namespace LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;

/// <summary>
/// Renders both bodies of a transactional message from one model. The HTML is table-based with
/// every rule inlined, because no mail client is required to honour a <c>&lt;style&gt;</c> block,
/// and the palette is the frontend's OKLCH design tokens converted to hex — mail clients do not
/// support OKLCH.
/// </summary>
/// <remarks>
/// The layout is dark, so link colour cannot be left to the client: clients auto-link bare domains
/// and addresses in body text and paint those anchors their own default blue, which is unreadable
/// here. Two defences, because either alone has a gap — a <c>&lt;style&gt;</c> override recolours
/// links this renderer never created, and the anchors it does create carry the colour inline for
/// clients that drop the block.
/// </remarks>
internal sealed class EmailTemplateRenderer : IEmailTemplateRenderer
{
    private const string FontStack =
        "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";

    private const string ColorBackground = "#100d08";
    private const string ColorSurface = "#201c16";
    private const string ColorBorder = "#3c372f";
    private const string ColorText = "#f4f1ec";
    private const string ColorTextMuted = "#c1bdb5";
    private const string ColorTextDim = "#9c988f";
    private const string ColorAccent = "#d9b160";
    private const string ColorAccentInk = "#100d08";

    private readonly string? _homeUrl;

    public EmailTemplateRenderer(IOptions<OpenIddictSettings> openIddictSettings)
    {
        ArgumentNullException.ThrowIfNull(openIddictSettings);

        _homeUrl = FrontendUrl.For(openIddictSettings.Value.WebClient, "/");
    }

    public EmailBody Render(EmailTemplateModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        return new EmailBody(RenderHtml(model), RenderPlainText(model));
    }

    /// <summary>
    /// Wins over a client's own link colour, including on anchors it generates itself. The button
    /// keeps dark ink, or the blanket rule would paint gold text onto a gold background.
    /// </summary>
    private static string RenderStyleOverrides() =>
        $$"""
          <style>
          a{color:{{ColorAccent}} !important;}
          a.cta,a.cta:visited,a.cta:hover{color:{{ColorAccentInk}} !important;text-decoration:none !important;}
          a.brand,a.brand:visited{text-decoration:none !important;}
          a[x-apple-data-detectors]{color:{{ColorAccent}} !important;}
          </style>
          """;

    private string RenderBrand()
    {
        if (_homeUrl is null)
        {
            return EmailBranding.Name;
        }

        string href = WebUtility.HtmlEncode(_homeUrl);
        return $"""<a class="brand" href="{href}" style="color:{ColorAccent};text-decoration:none;">{EmailBranding.Name}</a>""";
    }

    private string RenderHtml(EmailTemplateModel model)
    {
        string heading = WebUtility.HtmlEncode(model.Heading);
        StringBuilder builder = new();

        builder.Append(
            $"""
             <!DOCTYPE html>
             <html lang="pl">
             <head>
             <meta charset="utf-8">
             <meta name="viewport" content="width=device-width,initial-scale=1">
             <title>{heading}</title>
             {RenderStyleOverrides()}
             </head>
             <body style="margin:0;padding:0;background-color:{ColorBackground};">
             <div style="display:none;font-size:1px;line-height:1px;max-height:0;max-width:0;opacity:0;overflow:hidden;color:{ColorBackground};">{WebUtility.HtmlEncode(model.Preheader)}</div>
             <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background-color:{ColorBackground};">
             <tr><td align="center" style="padding:32px 16px;">
             <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="560" style="width:100%;max-width:560px;background-color:{ColorSurface};border:1px solid {ColorBorder};border-radius:14px;">
             <tr><td style="padding:28px 32px 0;font-family:{FontStack};font-size:15px;font-weight:700;letter-spacing:0.02em;color:{ColorAccent};">{RenderBrand()}</td></tr>
             <tr><td style="padding:18px 32px 0;font-family:{FontStack};font-size:22px;line-height:1.3;font-weight:700;color:{ColorText};">{heading}</td></tr>
             """);

        foreach (string paragraph in model.Paragraphs)
        {
            builder.Append(
                $"""

                 <tr><td style="padding:16px 32px 0;font-family:{FontStack};font-size:15px;line-height:1.6;color:{ColorTextMuted};">{WebUtility.HtmlEncode(paragraph)}</td></tr>
                 """);
        }

        if (model.CallToAction is { } callToAction)
        {
            string href = WebUtility.HtmlEncode(callToAction.Url);

            builder.Append(
                $"""

                 <tr><td style="padding:26px 32px 0;">
                 <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
                 <td align="center" bgcolor="{ColorAccent}" style="background-color:{ColorAccent};border-radius:8px;">
                 <a class="cta" href="{href}" style="display:inline-block;padding:13px 28px;font-family:{FontStack};font-size:15px;font-weight:700;color:{ColorAccentInk};text-decoration:none;border-radius:8px;">{WebUtility.HtmlEncode(callToAction.Label)}</a>
                 </td></tr></table>
                 </td></tr>
                 <tr><td style="padding:18px 32px 0;font-family:{FontStack};font-size:13px;line-height:1.6;color:{ColorTextDim};">
                 Jeśli przycisk nie działa, skopiuj ten adres do przeglądarki:<br>
                 <a href="{href}" style="color:{ColorAccent};word-break:break-all;">{href}</a>
                 </td></tr>
                 """);
        }

        if (!string.IsNullOrWhiteSpace(model.SecurityNote))
        {
            builder.Append(
                $"""

                 <tr><td style="padding:24px 32px 0;"><div style="border-top:1px solid {ColorBorder};font-size:0;line-height:0;">&nbsp;</div></td></tr>
                 <tr><td style="padding:16px 32px 0;font-family:{FontStack};font-size:13px;line-height:1.6;color:{ColorTextDim};">{WebUtility.HtmlEncode(model.SecurityNote)}</td></tr>
                 """);
        }

        builder.Append(
            $"""

             <tr><td style="padding:28px;"></td></tr>
             </table>
             <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="560" style="width:100%;max-width:560px;">
             <tr><td align="center" style="padding:18px 32px 0;font-family:{FontStack};font-size:12px;line-height:1.6;color:{ColorTextDim};">
             Wiadomość wysłana automatycznie — prosimy na nią nie odpowiadać.<br>
             {RenderBrand()} — {EmailBranding.Tagline}
             </td></tr>
             </table>
             </td></tr>
             </table>
             </body>
             </html>
             """);

        return builder.ToString();
    }

    private static string RenderPlainText(EmailTemplateModel model)
    {
        StringBuilder builder = new();

        builder.Append(EmailBranding.Name).Append("\n\n");
        builder.Append(model.Heading).Append("\n\n");

        foreach (string paragraph in model.Paragraphs)
        {
            builder.Append(paragraph).Append("\n\n");
        }

        if (model.CallToAction is { } callToAction)
        {
            builder.Append(callToAction.Label).Append(":\n").Append(callToAction.Url).Append("\n\n");
        }

        if (!string.IsNullOrWhiteSpace(model.SecurityNote))
        {
            builder.Append(model.SecurityNote).Append("\n\n");
        }

        builder.Append("--\n");
        builder.Append("Wiadomość wysłana automatycznie — prosimy na nią nie odpowiadać.\n");
        builder.Append(EmailBranding.Name).Append(" — ").Append(EmailBranding.Tagline).Append('\n');

        return builder.ToString();
    }
}
