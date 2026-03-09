using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.Cli.Commands;
using LotroKoniecDev.Infrastructure;
using LotroKoniecDev.Infrastructure.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Spectre.Console.Cli;

namespace LotroKoniecDev.Cli;

public sealed class Program
{
    private static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
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
        return result;
    }
}
