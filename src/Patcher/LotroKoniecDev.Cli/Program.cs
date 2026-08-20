using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Cli.Commands;
using LotroKoniecDev.Infrastructure;
using LotroKoniecDev.Cli.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli;

public sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        // The file sink stays at Debug on purpose: it is the trail users attach to a bug report, full
        // paths included, and the log never leaves their machine unless they send it. The console
        // stays at Information so the CLI output stays readable (AUDIT-SEC-07, #397).
        string logFilePath = Path.Combine(GlobalSettings.DataDir, "patcher.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
            .WriteTo.File(logFilePath,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        CancellationTokenSource cancellationTokenSource = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellationTokenSource.Cancel();
            Console.WriteLine("Cancellation requested...");
        };

        ServiceCollection services = new();
        services.AddLogging(builder => builder.AddSerilog());
        services.AddApplicationServices();
        services.AddInfrastructureServices();
        services.AddCliServices();

        TypeRegistrar typeRegistrar = new(services);
        CommandApp app = new(typeRegistrar);

        app.Configure(config =>
        {
            config.SetApplicationName("LotroKoniecDev");

            config.AddCommand<ExportCommand>("export")
                  .WithDescription("EXPORT texts from game");

            config.AddCommand<PatchCommand>("patch")
                  .WithDescription("PATCH (inject translations)");

            config.AddCommand<LaunchCommand>("launch")
                  .WithDescription("LAUNCH (patch + protect + play)");
        });

        int result = await app.RunAsync(args, cancellationTokenSource.Token);

        // Spectre.Console.Cli returns -1 when it cannot parse the command line, for example on an
        // unknown command or a missing argument. Map that to our own InvalidArguments exit code.
        return result < 0 ? ExitCodes.InvalidArguments : result;
    }
}
