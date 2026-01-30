using LotroKoniecDev.Application.Extensions;
using LotroKoniecDev.CLI.Errors;
using LotroKoniecDev.CLI.Guards;
using LotroKoniecDev.CLI.Services;
using LotroKoniecDev.CLI.UI;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Infrastructure;
using LotroKoniecDev.Primitives.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace LotroKoniecDev.CLI;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        UserInterfacePrinter.PrintBanner();

        if (!ArgsValidator.IsValid(args))
        {
            return (int)ExitCodes.InvalidArgumentsCount;
        }
        
        ServiceCollection services = new();
        services.AddApplicationServices();
        services.AddInfrastructureServices();
        services.AddCliServices();

        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        
        App app = serviceProvider.GetRequiredService<App>();
        
        Result result = await app.RunAsync(args);
        if (result.IsSuccess)
        {
            return (int)ExitCodes.Success;
        }
        
        return result.Error.Type switch
        {
            ErrorType.Validation => (int)ExitCodes.InvalidArgumentsCount,
            _ => (int)ExitCodes.OperationFailed,
        };
    }
}
