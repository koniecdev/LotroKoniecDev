namespace LotroKoniecDev.Tests.E2E;

/// <summary>
/// Mirrors CLI exit codes for readable E2E assertions.
/// Keep in sync with src/LotroKoniecDev.Cli/ExitCodes.cs.
/// </summary>
public enum CliExitCode
{
    Success = 0,
    InvalidArguments = 1,
    FileNotFound = 2,
    OperationFailed = 3
}
