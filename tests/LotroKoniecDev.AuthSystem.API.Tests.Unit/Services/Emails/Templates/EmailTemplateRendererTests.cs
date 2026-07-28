using LotroKoniecDev.AuthSystem.API.Services.Emails.Templates;
using LotroKoniecDev.AuthSystem.Infrastructure.Emails;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Services.Emails.Templates;

public sealed class EmailTemplateRendererTests
{
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
        EmailTemplateRenderer renderer = new();
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
        EmailTemplateRenderer renderer = new();
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
        EmailTemplateRenderer renderer = new();
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
        EmailTemplateRenderer renderer = new();
        const string url = "https://auth.lotro-translator.pl/Account/ConfirmEmail?token=abc";
        EmailTemplateModel model = BuildModel(new EmailCallToAction("Potwierdź konto", url));

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.PlainText.ShouldContain("Potwierdź konto:");
        body.PlainText.ShouldContain(url);
    }

    [Fact]
    public void Render_ModelWithoutCallToAction_RendersNoLink()
    {
        // Arrange
        EmailTemplateRenderer renderer = new();
        EmailTemplateModel model = BuildModel(callToAction: null);

        // Act
        EmailBody body = renderer.Render(model);

        // Assert
        body.Html.ShouldNotContain("<a href=");
        body.Html.ShouldNotContain("Jeśli przycisk nie działa");
        body.PlainText.ShouldNotContain("http");
    }

    [Fact]
    public void Render_MarkupInModelText_IsHtmlEncodedButLeftRawInPlainText()
    {
        // Arrange
        EmailTemplateRenderer renderer = new();
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
        EmailTemplateRenderer renderer = new();
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
        EmailTemplateRenderer renderer = new();
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
        EmailTemplateRenderer renderer = new();
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
        EmailTemplateRenderer renderer = new();
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
