using LotroKoniecDev.Application.Abstractions;
using LotroKoniecDev.Application.Abstractions.DatFilesServices;
using LotroKoniecDev.Application.Features.Patch;
using LotroKoniecDev.Domain.Core.Monads;
using LotroKoniecDev.Domain.Models;
using LotroKoniecDev.Cli.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using static LotroKoniecDev.Cli.ConsoleWriter;

namespace LotroKoniecDev.Cli.Commands;

internal static class PatchCommand
{
    // internal sealed record Args(TranslationPath TranslationPath, DatPath? DatPath = null);

    private const string TranslationsDir = "translations";

    public static async Task<int> RunAsync(
        TranslationPath translationPathArg,
        DatPath? datPathArg,
        IServiceProvider serviceProvider,
        string versionFilePath)
    {
        IDatPathResolver datPathResolver = serviceProvider.GetRequiredService<IDatPathResolver>();
        string translationsPath = ResolveTranslationsPath(translationPathArg.Path);

        string? datPath = datPathResolver.Resolve(datPathArg?.Path);

        if (datPath is null)
        {
            return ExitCodes.FileNotFound;
        }

        if (!File.Exists(datPath))
        {
            WriteError($"DAT file not found: {datPath}");
            return ExitCodes.FileNotFound;
        }
        
        if (!File.Exists(translationsPath))
        {
            WriteError($"Translation file not found: {translationsPath}");
            return ExitCodes.FileNotFound;
        }


        // Early validation: parse translations before expensive/interactive preflight checks.
        // This prevents blocking on Console.ReadLine() prompts for invalid input files.
        WriteInfo($"Loading translations from: {translationsPath}");
        ITranslationParser parser = serviceProvider.GetRequiredService<ITranslationParser>();
        Result<IReadOnlyList<Translation>> parseResult = parser.ParseFile(translationsPath);

        if (parseResult.IsFailure)
        {
            WriteError(parseResult.Error.Message);
            return ExitCodes.OperationFailed;
        }

        if (parseResult.Value.Count == 0)
        {
            WriteError("No valid translations found in file.");
            return ExitCodes.OperationFailed;
        }

        IPreflightChecker preflightChecker = serviceProvider.GetRequiredService<IPreflightChecker>();
        if (!await preflightChecker.RunAllAsync(datPath, versionFilePath))
        {
            return ExitCodes.OperationFailed;
        }

        IBackupManager backupManager = serviceProvider.GetRequiredService<IBackupManager>();
        Result backupResult = backupManager.Create(datPath);
        if (backupResult.IsFailure)
        {
            WriteError(backupResult.Error.Message);
            return ExitCodes.OperationFailed;
        }

        using IServiceScope scope = serviceProvider.CreateScope();
        IPatcher patcher = scope.ServiceProvider.GetRequiredService<IPatcher>();

        Result<PatchSummaryResponse> result = patcher.ApplyTranslations(
            translationsPath,
            datPath,
            (applied, total) => WriteProgress($"Patching... {applied}/{total}"));

        if (result.IsFailure)
        {
            WriteError(result.Error.Message);
            backupManager.Restore(datPath);
            return ExitCodes.OperationFailed;
        }

        PatchSummaryResponse summaryResponse = result.Value;

        foreach (string warning in summaryResponse.Warnings.Take(10))
        {
            WriteWarning(warning);
        }

        if (summaryResponse.Warnings.Count > 10)
        {
            WriteWarning($"... and {summaryResponse.Warnings.Count - 10} more warnings");
        }

        Console.WriteLine();
        WriteSuccess("=== PATCH COMPLETE ===");
        WriteInfo($"Applied {summaryResponse.AppliedTranslations:N0} of {summaryResponse.TotalTranslations:N0} translations");

        if (summaryResponse.SkippedTranslations > 0)
        {
            WriteWarning($"Skipped: {summaryResponse.SkippedTranslations:N0}");
        }

        return ExitCodes.Success;
    }

    private static string ResolveTranslationsPath(string input)
    {
        return input.Contains(Path.DirectorySeparatorChar) ||
               input.Contains(Path.AltDirectorySeparatorChar) ||
               input.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            ? input
            : Path.Combine(TranslationsDir, input + ".txt");
    }
}
