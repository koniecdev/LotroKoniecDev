using System.Diagnostics;

namespace LotroKoniecDev.Tests.E2E;

[CollectionDefinition("E2E")]
public sealed class E2ETestCollection : ICollectionFixture<E2ETestFixture>;

public sealed class E2ETestFixture : IAsyncLifetime
{
    private const string DatFileName = "client_local_English.dat";
    private const string CliProjectName = "LotroKoniecDev.Cli";

    private readonly List<string> _tempDirectories = [];

    public string DatFilePath { get; private set; } = string.Empty;
    public string CliExePath { get; private set; } = string.Empty;
    public string TranslationsPolishPath { get; private set; } = string.Empty;
    public bool IsDatFileAvailable { get; private set; }
    public string CachedExportPath { get; private set; } = string.Empty;
    public CliResult? CachedExportResult { get; private set; }

    public async Task InitializeAsync()
    {
        string testDataDir = FindTestDataDirectory();
        DatFilePath = Path.Combine(testDataDir, DatFileName);

        if (!File.Exists(DatFilePath))
        {
            IsDatFileAvailable = false;
            return;
        }
        await BuildCliProjectAsync();
        CliExePath = FindCliExe();
        TranslationsPolishPath = FindTranslationsFile("polish.txt");
        IsDatFileAvailable = true;

        // Clean up orphaned temp dirs from previous crashed runs (older than 1h)
        foreach (string dir in Directory.GetDirectories(Path.GetTempPath(), "lotro_e2e_*"))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddHours(-1))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // ignored
            }
        }

        // Pre-export once for all tests to share
        string exportDir = CreateTempDir();
        CachedExportPath = Path.Combine(exportDir, "export.txt");
        CachedExportResult = await RunCliAsync($"export \"{DatFilePath}\" \"{CachedExportPath}\"");

        if (CachedExportResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Cached export failed with exit code {CachedExportResult.ExitCode}. " +
                $"stderr: {CachedExportResult.Stderr}");
        }
    }

    public Task DisposeAsync()
    {
        foreach (string dir in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        return Task.CompletedTask;
    }

    public async Task<CliResult> RunCliAsync(string args, int timeoutSeconds = 120, string? workingDirectory = null)
    {
        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = CliExePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDirectory is not null)
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }

        // The x64 test runner sets DOTNET_ROOT to the x64 path. The x86 CLI exe
        // inherits this and tries to load x64 hostfxr.dll, which fails.
        // Clearing it lets the x86 apphost find its runtime via registry probing.
        process.StartInfo.Environment.Remove("DOTNET_ROOT");

        process.Start();

        // Close stdin immediately so any Console.ReadLine() prompts
        process.StandardInput.Close();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"CLI process did not exit within {timeoutSeconds}s. Args: {args}");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        return new CliResult(process.ExitCode, stdout, stderr);
    }

    public string CreateTempDatCopy()
    {
        string tempDir = CreateTempDir();
        string destPath = Path.Combine(tempDir, DatFileName);
        File.Copy(DatFilePath, destPath);
        return destPath;
    }

    public string CreateTempDir()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"lotro_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        _tempDirectories.Add(tempDir);
        return tempDir;
    }

    private static async Task BuildCliProjectAsync()
    {
        string solutionRoot = FindSolutionRoot();
        string cliProjectPath = Path.Combine(solutionRoot, "src", CliProjectName);

        using Process process = new();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{cliProjectPath}\" --no-restore -v q",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            throw new InvalidOperationException(
                $"CLI build failed (exit code {process.ExitCode}).\nstdout: {stdout}\nstderr: {stderr}");
        }
    }

    private static string FindTestDataDirectory()
    {
        string assemblyDir = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(assemblyDir);

        while (dir is not null)
        {
            string testDataPath = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(testDataPath)
                && File.Exists(Path.Combine(dir.FullName, "LotroKoniecDev.Tests.E2E.csproj")))
            {
                return testDataPath;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find TestData directory. Ensure you are running from the E2E test project.");
    }

    private static string FindCliExe()
    {
        string solutionRoot = FindSolutionRoot();
        string cliBinDir = Path.Combine(solutionRoot, "src", CliProjectName, "bin");

        if (!Directory.Exists(cliBinDir))
        {
            throw new InvalidOperationException(
                $"CLI bin directory not found at {cliBinDir}. Build the CLI first: dotnet build src/{CliProjectName}");
        }

        string exeName = $"{CliProjectName}.exe";
        string exePath = Directory.GetFiles(cliBinDir, exeName, SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault() 
            ?? throw new InvalidOperationException(
                $"CLI exe '{exeName}' not found under {cliBinDir}. Build the CLI first: dotnet build src/{CliProjectName}");

        return exePath;
    }

    private static string FindTranslationsFile(string fileName)
    {
        string solutionRoot = FindSolutionRoot();
        string path = Path.Combine(solutionRoot, "translations", fileName);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Translations file not found at {path}.");
        }

        return path;
    }

    private static string FindSolutionRoot()
    {
        string assemblyDir = AppContext.BaseDirectory;
        DirectoryInfo? dir = new(assemblyDir);

        while (dir is not null)
        {
            if (dir.GetFiles("*.slnx").Length > 0 || dir.GetFiles("*.sln").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find solution root. Ensure the .slnx file exists.");
    }
}

public sealed record CliResult(int ExitCode, string Stdout, string Stderr);
