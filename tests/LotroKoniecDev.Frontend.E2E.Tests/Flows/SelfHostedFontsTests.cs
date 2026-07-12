using System.Collections.Concurrent;
using LotroKoniecDev.Frontend.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Shouldly;

namespace LotroKoniecDev.Frontend.E2E.Tests.Flows;

/// <summary>
/// LEGAL-06 — web fonts are self-hosted: no browser request may leave to
/// <c>fonts.googleapis.com</c> / <c>fonts.gstatic.com</c> (or any other Google host) from either
/// the frontend or the auth hosted pages, and the vendored Manrope/JetBrains Mono files must
/// actually load so the same families keep rendering. Needs no account and no seeded database.
/// </summary>
public sealed class SelfHostedFontsTests : E2ETestBase
{
    public SelfHostedFontsTests(PlaywrightStackFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task Frontend_and_auth_pages_load_fonts_locally_and_send_nothing_to_google()
    {
        ConcurrentBag<string> requestedUrls = [];
        Page.Request += (_, request) => requestedUrls.Add(request.Url);

        string[] pages =
        [
            $"{Fixture.FrontendBaseUrl}/",
            $"{Fixture.FrontendBaseUrl}/regulamin",
            $"{Fixture.AuthBaseUrl}/Account/Login",
            $"{Fixture.AuthBaseUrl}/Account/Register",
            $"{Fixture.AuthBaseUrl}/Account/PrivacyPolicy"
        ];

        foreach (string url in pages)
        {
            await Page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        }

        // Force both families (Polish sample → the latin-ext subset too); unicode-range faces
        // only download once text uses them, so an explicit load makes the fetch deterministic.
        bool bothFamiliesLoaded = await Page.EvaluateAsync<bool>(
            """
            async () => {
                await document.fonts.load("700 16px Manrope", "Zażółć gęślą jaźń");
                await document.fonts.load("400 16px 'JetBrains Mono'", "Zażółć gęślą jaźń");
                return document.fonts.check("700 16px Manrope")
                    && document.fonts.check("400 16px 'JetBrains Mono'");
            }
            """);

        requestedUrls.ShouldNotBeEmpty();
        requestedUrls.Where(url => url.Contains("googleapis.com") || url.Contains("gstatic.com"))
            .ShouldBeEmpty();

        // Visual parity: the same families render, fetched from our own origins.
        bothFamiliesLoaded.ShouldBeTrue();
        requestedUrls.Count(url => url.Contains("/fonts/manrope-", StringComparison.Ordinal)).ShouldBeGreaterThan(0);
        requestedUrls.Count(url => url.Contains("/fonts/jetbrains-mono-", StringComparison.Ordinal)).ShouldBeGreaterThan(0);
    }
}
