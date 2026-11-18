using System.Globalization;
using LotroKoniecDev.DesktopApp;
using Serilog.Events;
using Serilog.Formatting.Display;
using Serilog.Parsing;

namespace LotroKoniecDev.Tests.Infrastructure.Tests;

/// <summary>The app's log keeps one file, so every line says on which day it was written (owner decision 4 on #42).</summary>
public sealed class DesktopAppLogTemplateTests
{
    [Fact]
    public void LogOutputTemplate_AnyLine_StartsWithTheDateAndTheTime()
    {
        // Arrange
        MessageTemplateTextFormatter formatter = new(Program.LogOutputTemplate, CultureInfo.InvariantCulture);
        LogEvent logEvent = new(
            new DateTimeOffset(2026, 11, 18, 9, 30, 15, 123, TimeSpan.Zero),
            LogEventLevel.Information,
            null,
            new MessageTemplateParser().Parse("Patch started"),
            []);
        using StringWriter output = new(CultureInfo.InvariantCulture);

        // Act
        formatter.Format(logEvent, output);

        // Assert
        output.ToString().ShouldBe($"2026-11-18 09:30:15.123 [INF] Patch started{Environment.NewLine}");
    }
}
