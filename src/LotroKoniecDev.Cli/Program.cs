using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Cli.Commands;
using LotroKoniecDev.Infrastructure;
using LotroKoniecDev.Cli.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli;

public sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        string logFilePath = Path.Combine(GlobalSettings.DataDir, "launch_test.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
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

        // Spectre.Console.Cli returns -1 for parse errors (unknown command, missing args).
        // Map to our InvalidArguments exit code for consistent contract.
        return result < 0 ? ExitCodes.InvalidArguments : result;
    }
}
