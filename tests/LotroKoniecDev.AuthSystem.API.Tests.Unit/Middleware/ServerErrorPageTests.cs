using LotroKoniecDev.AuthSystem.API.Middleware;

namespace LotroKoniecDev.AuthSystem.API.Tests.Unit.Middleware;

public sealed class ServerErrorPageTests
{
    [Fact]
    public void BuildHtml_ShouldTellTheUserTheFaultIsOnOurSide()
    {
        // Act
        string html = ServerErrorPage.BuildHtml(nonce: null);

        // Assert
        html.ShouldContain("<h1>Coś poszło nie tak</h1>");
        html.ShouldContain("Spróbuj ponownie za chwilę.");
        html.ShouldContain(BrowserErrorPage.BackToLoginLink);
    }
}
