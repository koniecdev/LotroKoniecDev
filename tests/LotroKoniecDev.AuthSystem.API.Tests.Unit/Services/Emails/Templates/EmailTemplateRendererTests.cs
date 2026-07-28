using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.API.Settings;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails.Templates;

public sealed class EmailTemplateRendererTests
{
    private const string AppRoot = "https://lotro-translator.pl";

    private static EmailTemplateRenderer BuildRenderer(string? appRoot = AppRoot) =>
        new(Microsoft.Extensions.Options.Options.Create(new OpenIddictSettings
        {
            Issuer = "https://auth.lotro-translator.pl",
            WebClient = new WebClientSettings
            {
                PostLogoutRedirectUris = appRoot is null ? [] : [appRoot]
            }
        }));

    private static EmailTemplateModel BuildModel(
        EmailCallToAction? callToAction = null,
        string? securityNote = null,
        string heading = "Reset hasła",
        IReadOnlyList<string>? paragraphs = null) =>
        new()
        {
            Preheader = "Ustaw nowe hasło do swojego konta.",
            Heading = heading,
            Paragraphs = paragraphs ?? ["Otrzymaliśmy prośbę o zresetowanie hasła."],
            CallToAction = callToAction,
            SecurityNote = securityNote
        };

    [Fact]
    public void Render_AnyModel_PutsHeadingAndParagraphsInBothBodies()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel(heading: "Potwierdź swoje konto", paragraphs: ["Dziękujemy za rejestrację."]);

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain("Potwierdź swoje konto");
        body.Html.ShouldContain("Dziękujemy za rejestrację.");
        body.PlainText.ShouldContain("Potwierdź swoje konto");
        body.PlainText.ShouldContain("Dziękujemy za rejestrację.");
    }

    [Fact]
    public void Render_ModelWithCallToAction_RendersTheLabelAsAnHtmlLink()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel(
            new EmailCallToAction("Ustaw nowe hasło", "https://auth.lotro-translator.pl/Account/ResetPassword"));

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain("href=\"https://auth.lotro-translator.pl/Account/ResetPassword\"");
        body.Html.ShouldContain("Ustaw nowe hasło");
    }

    [Fact]
    public void Render_CallToActionUrlWithQueryParameters_EscapesAmpersandsInHtmlOnly()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        const string url = "https://auth.lotro-translator.pl/Account/ResetPassword?email=a%40b.pl&token=CfDJ8A";
        EmailTemplateModel model = BuildModel(new EmailCallToAction("Ustaw nowe hasło", url));

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain("&amp;token=CfDJ8A");
        body.Html.ShouldNotContain("&token=CfDJ8A");
        body.PlainText.ShouldContain(url);
    }

    [Fact]
    public void Render_CallToAction_RepeatsTheUrlAsCopyablePlainText()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        const string url = "https://auth.lotro-translator.pl/Account/ConfirmEmail?token=abc";
        EmailTemplateModel model = BuildModel(new EmailCallToAction("Potwierdź konto", url));

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.PlainText.ShouldContain("Potwierdź konto:");
        body.PlainText.ShouldContain(url);
    }

    [Fact]
    public void Render_ModelWithoutCallToAction_RendersNoButtonOrFallbackLink()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel(callToAction: null);

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldNotContain("class=\"cta\"");
        body.Html.ShouldNotContain("Jeśli przycisk nie działa");
        body.PlainText.ShouldNotContain("http");
    }

    [Fact]
    public void Render_Always_OverridesTheClientDefaultLinkColour()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel();

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain("a{color:#d9b160 !important;}");
        body.Html.ShouldContain("a[x-apple-data-detectors]{color:#d9b160 !important;}");
    }

    [Fact]
    public void Render_CallToAction_ExemptsTheButtonFromTheGoldOverride()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel(
            new EmailCallToAction("Ustaw nowe hasło", "https://auth.lotro-translator.pl/Account/ResetPassword"));

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain("a.cta,a.cta:visited,a.cta:hover{color:#100d08 !important;");
        body.Html.ShouldContain("<a class=\"cta\"");
    }

    [Fact]
    public void Render_ConfiguredWebClient_TurnsTheBrandIntoAGoldLink()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer(AppRoot);
        EmailTemplateModel model = BuildModel();

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain($"<a class=\"brand\" href=\"{AppRoot}/\" style=\"color:#d9b160;");
        body.Html.ShouldContain(EmailBranding.Name);
    }

    [Fact]
    public void Render_UnconfiguredWebClient_LeavesTheBrandAsPlainText()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer(appRoot: null);
        EmailTemplateModel model = BuildModel();

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldNotContain("class=\"brand\"");
        body.Html.ShouldContain(EmailBranding.Name);
    }

    [Fact]
    public void Render_MarkupInModelText_IsHtmlEncodedButLeftRawInPlainText()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel(
            heading: "<script>alert(1)</script>",
            paragraphs: ["Tekst z <b>znacznikiem</b>"]);

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldNotContain("<script>");
        body.Html.ShouldContain("&lt;script&gt;");
        body.Html.ShouldContain("&lt;b&gt;znacznikiem&lt;/b&gt;");
        body.PlainText.ShouldContain("<script>alert(1)</script>");
    }

    [Fact]
    public void Render_MarkupInCallToAction_IsHtmlEncoded()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel(
            new EmailCallToAction("\"><script>alert(1)</script>", "https://auth.lotro-translator.pl/\"><script>"));

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldNotContain("<script>");
        body.Html.ShouldContain("&lt;script&gt;");
        body.Html.ShouldContain("&quot;&gt;");
    }

    [Fact]
    public void Render_ModelWithSecurityNote_ShowsItInBothBodies()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        const string note = "Jeśli to nie Ty prosiłeś(-aś) o reset hasła, zignoruj tę wiadomość.";
        EmailTemplateModel model = BuildModel(securityNote: note);

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain(note);
        body.PlainText.ShouldContain(note);
    }

    [Fact]
    public void Render_ModelWithoutSecurityNote_OmitsTheNoteSection()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel(securityNote: null);

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldNotContain("border-top:1px solid");
    }

    [Fact]
    public void Render_AnyModel_BrandsBothBodiesAndHidesThePreheader()
    {
        // Arrange
        EmailTemplateRenderer renderer = BuildRenderer();
        EmailTemplateModel model = BuildModel();

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldContain(EmailBranding.Name);
        body.Html.ShouldContain("Ustaw nowe hasło do swojego konta.");
        body.Html.ShouldContain("display:none");
        body.PlainText.ShouldContain(EmailBranding.Name);
    }
}
