using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The source tree an E2E run tests, and the Docker images the run builds from it (#884). The file is
/// linked into both E2E suites, so they share one rule for naming their images.
/// </summary>
/// <remarks>
/// <para>
/// The images used to have one fixed tag on every machine. A second worktree that built at the same
/// time moved that tag, and the first run then started its containers from the other worktree's code.
/// So the tag now names the worktree: the folder name, for a person who reads <c>docker images</c>, and
/// a hash of the full path, because two clones can have the same folder name.
/// </para>
/// <para>
/// The tag belongs to the worktree and not to one run. That keeps <c>SKIP_DOCKER_BUILD=true</c> useful:
/// it reuses the images this worktree built last time. The price is that one image set stays behind per
/// worktree, until someone removes it by <see cref="ImageLabel"/>.
/// </para>
/// </remarks>
internal sealed class E2EWorktree
{
    /// <summary>
    /// Every image an E2E fixture builds carries this label. It lets the fixture find its own untagged
    /// leftovers, and it lets a person list or remove the images of a worktree that no longer exists.
    /// </summary>
    public const string ImageLabel = "lotrokoniecdev.e2e";

    private const string SkipBuildVariable = "SKIP_DOCKER_BUILD";
    private const int FolderNameMaxLength = 40;
    private const int PathHashLength = 8;

    public E2EWorktree(string suite)
    {
        SolutionDirectory = FindSolutionDirectory();
        ImageTag = BuildImageTag(suite, SolutionDirectory);
    }

    public string SolutionDirectory { get; }

    public string ImageTag { get; }

    public string ImageName(string repository) => $"{repository}:{ImageTag}";

    /// <summary>
    /// Builds each image from this worktree. With <c>SKIP_DOCKER_BUILD=true</c> it builds nothing and
    /// only checks that this worktree already has the images.
    /// </summary>
    public async Task BuildImagesAsync(IReadOnlyList<(string Repository, string Dockerfile)> images)
    {
        string[] imageNames = images.Select(image => ImageName(image.Repository)).ToArray();

        if (Environment.GetEnvironmentVariable(SkipBuildVariable) == "true")
        {
            Console.WriteLine($"Skipping Docker image build ({SkipBuildVariable}=true). Reusing the images tagged {ImageTag}.");
            await EnsureImagesExistAsync(imageNames);
            return;
        }

        string cacheBust = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        foreach ((string repository, string dockerfile) in images)
        {
            string imageName = ImageName(repository);
            Console.WriteLine($"Building Docker image: {imageName}...");

            DockerResult result = await RunDockerAsync(
                "build", "--build-arg", $"CACHEBUST={cacheBust}", "--label", ImageLabel,
                "-f", dockerfile, "-t", imageName, ".");

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to build Docker image '{imageName}' (exit code {result.ExitCode}).\n" +
                    $"Dockerfile: {dockerfile}\nWorking directory: {SolutionDirectory}\n" +
                    $"Stdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
            }

            Console.WriteLine($"Successfully built: {imageName}");
        }

        await RemoveUntaggedImagesAsync();
    }

    private async Task EnsureImagesExistAsync(string[] imageNames)
    {
        DockerResult result = await RunDockerAsync(["image", "inspect", "--format", "{{.Id}}", .. imageNames]);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{SkipBuildVariable}=true, but this worktree does not have all of its images yet: " +
                $"{string.Join(", ", imageNames)}.\n" +
                $"Run the suite once without {SkipBuildVariable} to build them.\nStderr:\n{result.Stderr}");
        }
    }

    /// <summary>
    /// A rebuild moves the tag to the new image. On some Docker setups the old image then stays on disk
    /// without a name, so every run would leave one more image set behind. Prune removes only such
    /// untagged images, and never one that a container still uses, so it cannot break a run that is
    /// going on in another worktree.
    /// </summary>
    private async Task RemoveUntaggedImagesAsync()
    {
        DockerResult result = await RunDockerAsync("image", "prune", "--force", "--filter", $"label={ImageLabel}");

        if (result.ExitCode != 0)
        {
            // Only disk space is at stake here, so a failed clean-up must not fail the whole suite.
            Console.WriteLine($"Could not remove untagged E2E images (exit code {result.ExitCode}).\nStderr:\n{result.Stderr}");
        }
    }

    private async Task<DockerResult> RunDockerAsync(params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "docker",
            WorkingDirectory = SolutionDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new();
        process.StartInfo = startInfo;
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new DockerResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string BuildImageTag(string suite, string solutionDirectory)
    {
        string folderName = new(Path.GetFileName(solutionDirectory)
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '-')
            .Take(FolderNameMaxLength)
            .ToArray());

        string pathHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(solutionDirectory)))[..PathHashLength];

        return $"{suite}-{folderName}-{pathHash}";
    }

    private static string FindSolutionDirectory()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LotroKoniecDev.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the solution directory (LotroKoniecDev.slnx). Started from: {Directory.GetCurrentDirectory()}");
    }

    private sealed record DockerResult(int ExitCode, string Stdout, string Stderr);
}
