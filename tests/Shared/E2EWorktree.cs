using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The source tree an E2E run tests, and the Docker images the run builds from it (#884). The file is
/// linked into both E2E suites, so they share one rule for naming and starting their images.
/// </summary>
/// <remarks>
/// <para>
/// The images used to have one fixed tag on every machine. A second worktree that built at the same
/// time moved that tag while the first run was still starting containers, and the first run then tested
/// the other worktree's code. So the tag now names the worktree: the folder name, for a person who reads
/// <c>docker images</c>, and a hash of the full path, because two clones can have the same folder name.
/// </para>
/// <para>
/// A run also starts its containers from the image IDs it built or found, never from the tag, so it only
/// ever tests what it built. Two runs of the same suite in one worktree still share a tag (#889). The
/// second build takes the tag, and Docker's containerd image store, the Docker Desktop default, then
/// deletes the first run's image at once. The first run fails with "pull access denied for sha256"
/// instead of testing the other build.
/// </para>
/// <para>
/// The tag belongs to the worktree and not to one run, so <c>SKIP_DOCKER_BUILD=true</c> reuses the
/// images this worktree built last time, and a run leaves one image set per worktree behind. A person
/// removes the set of a deleted worktree by <see cref="ImageLabel"/>.
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

    private readonly string _imageTag;
    private readonly Dictionary<string, string> _imageIds = [];

    public E2EWorktree(string suite)
    {
        SolutionDirectory = FindSolutionDirectory();
        _imageTag = BuildImageTag(suite, SolutionDirectory);
    }

    public string SolutionDirectory { get; }

    /// <summary>The image ID a container of <paramref name="repository"/> must start from.</summary>
    public string Image(string repository) =>
        _imageIds.TryGetValue(repository, out string? imageId)
            ? imageId
            : throw new InvalidOperationException($"No image for '{repository}'. Call {nameof(BuildImagesAsync)} first.");

    /// <summary>
    /// Builds each image from this worktree. With <c>SKIP_DOCKER_BUILD=true</c> it builds nothing and
    /// takes the images this worktree already has.
    /// </summary>
    public async Task BuildImagesAsync(IReadOnlyList<(string Repository, string Dockerfile)> images)
    {
        if (Environment.GetEnvironmentVariable(SkipBuildVariable) == "true")
        {
            Console.WriteLine($"Skipping Docker image build ({SkipBuildVariable}=true). Reusing the images tagged {_imageTag}.");
            await FindExistingImagesAsync(images.Select(image => image.Repository).ToArray());
            return;
        }

        string cacheBust = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        foreach ((string repository, string dockerfile) in images)
        {
            _imageIds[repository] = await BuildImageAsync(repository, dockerfile, cacheBust);
        }

        await RemoveOldUntaggedImagesAsync();
    }

    private async Task<string> BuildImageAsync(string repository, string dockerfile, string cacheBust)
    {
        string imageName = ImageName(repository);
        string imageIdFile = Path.GetTempFileName();

        try
        {
            Console.WriteLine($"Building Docker image: {imageName}...");

            DockerResult result = await RunDockerAsync(
                "build", "--build-arg", $"CACHEBUST={cacheBust}", "--label", ImageLabel,
                "--iidfile", imageIdFile, "-f", dockerfile, "-t", imageName, ".");

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to build Docker image '{imageName}' (exit code {result.ExitCode}).\n" +
                    $"Dockerfile: {dockerfile}\nWorking directory: {SolutionDirectory}\n" +
                    $"Stdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
            }

            string imageId = (await File.ReadAllTextAsync(imageIdFile)).Trim();
            Console.WriteLine($"Successfully built: {imageName} ({imageId})");
            return imageId;
        }
        finally
        {
            File.Delete(imageIdFile);
        }
    }

    private async Task FindExistingImagesAsync(string[] repositories)
    {
        string[] imageNames = repositories.Select(ImageName).ToArray();
        DockerResult result = await RunDockerAsync(["image", "inspect", "--format", "{{.Id}}", .. imageNames]);
        string[] imageIds = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (result.ExitCode != 0 || imageIds.Length != repositories.Length)
        {
            throw new InvalidOperationException(
                $"{SkipBuildVariable}=true, but this worktree does not have all of its images yet: " +
                $"{string.Join(", ", imageNames)}.\n" +
                $"Run the suite once without {SkipBuildVariable} to build them.\nStderr:\n{result.Stderr}");
        }

        foreach ((string repository, string imageId) in repositories.Zip(imageIds))
        {
            _imageIds[repository] = imageId;
        }
    }

    /// <summary>
    /// On Docker's classic image store, a rebuild leaves the old image on disk without a name, so every
    /// run would leave one more image set behind. The containerd store deletes that image at once, so
    /// there this finds nothing. Prune removes only images that have no tag, that no container uses, and
    /// that are older than an hour. The age limit protects an image that the classic builder has made but
    /// not tagged yet, and an image that a run found minutes ago and has not started yet.
    /// </summary>
    private async Task RemoveOldUntaggedImagesAsync()
    {
        DockerResult result = await RunDockerAsync(
            "image", "prune", "--force", "--filter", $"label={ImageLabel}", "--filter", "until=1h");

        if (result.ExitCode != 0)
        {
            // Only disk space is at stake here, so a failed clean-up must not fail the whole suite.
            Console.WriteLine($"Could not remove untagged E2E images (exit code {result.ExitCode}).\nStderr:\n{result.Stderr}");
        }
    }

    private string ImageName(string repository) => $"{repository}:{_imageTag}";

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

        // Lower case first: Windows and macOS ignore case in paths, and two ways of starting the tests
        // can spell the same folder differently. The same worktree must always get the same tag.
        string pathHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(solutionDirectory.ToLowerInvariant())))[..PathHashLength];

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
