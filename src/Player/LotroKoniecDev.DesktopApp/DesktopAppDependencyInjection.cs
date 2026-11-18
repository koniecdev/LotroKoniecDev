using System.Reflection;
using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.DesktopApp.Presentation;
using LotroKoniecDev.DesktopApp.Presentation.Settings;
using LotroKoniecDev.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace LotroKoniecDev.DesktopApp;

/// <summary>
/// The composition root: the same patcher handlers and adapters the CLI uses, plus the desktop UI in
/// place of the console (ADR-0033 §3).
/// </summary>
internal static class DesktopAppDependencyInjection
{
    /// <summary>The assembly metadata key the csproj stamps the version under (spec 0016).</summary>
    internal const string VersionMetadataKey = "DesktopVersion";

    /// <summary>The settings sit next to the log file, in the app's own AppData folder (#43).</summary>
    internal static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LotroPoPolsku",
        "config.json");

    /// <remarks>
    /// The handlers' log lines go to Serilog's static logger, which <see cref="Program"/> points at the
    /// log file, as the CLI does.
    /// </remarks>
    public static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddApplicationServices();
        services.AddInfrastructureServices();
        services.AddPresentationServices();
        services.AddSingleton<ISettingsFile>(new SettingsFile(SettingsFilePath));
        services.AddSingleton<IFileProbe, FileProbe>();
        services.AddSingleton(new InstalledAppVersion(ReadStampedVersion()));

        // DI validation runs in every environment (#572). A handler that cannot be built stops the app
        // before any window opens, and Program writes the reason to the log file.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    /// <summary>A build with no stamp counts as a developer build, which never checks for updates.</summary>
    private static string ReadStampedVersion() =>
        typeof(DesktopAppDependencyInjection).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == VersionMetadataKey)?.Value
        ?? "0.0.0";
}
