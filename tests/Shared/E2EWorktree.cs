using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LotroKoniecDev.Tests.Shared;

/// <summary>
/// The source tree an E2E run tests, and the Docker images the run builds from it (#884). Linked into
/// both E2E suites, so they share one rule for naming and starting their images.
/// </summary>
/// <remarks>
/// <para>
/// The tag names the worktree, so a second worktree that builds at the same time cannot move it while
/// this run starts its containers. It holds the folder name, for a person who reads
/// <c>docker images</c>, and a hash of the full path, because two clones can have the same folder name.
/// </para>
/// <para>
/// A run starts its containers from the image IDs it built or found, never from the tag, so it only
/// tests what it built. The build never takes the tag (#889): on Docker's containerd image store (the
/// Docker Desktop default), <c>docker build -t</c> deletes the image that loses the tag, even when another
/// run of the suite in this worktree is about to start from it. A separate <c>docker tag</c> keeps that
/// image, untagged. Two <c>docker tag</c> calls for one name at the same moment can fail or lose an image,
/// so the tag moves only under <see cref="TagLockFile"/>.
/// </para>
/// <para>
/// Because the tag belongs to the worktree, <c>SKIP_DOCKER_BUILD=true</c> reuses the images this
/// worktree built last time. A run leaves the tagged image set of its worktree behind, plus the
/// untagged sets that its rebuilds replaced, until a later run finds them an hour old. A person removes
/// the images of a deleted worktree by <see cref="ImageLabel"/>.
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
    private const int TagLockTimeoutSeconds = 60;
    private const int TagLockRetryMilliseconds = 50;

    private readonly string _imageTag;
    private readonly bool _skipBuild;
    private readonly TimeSpan _tagLockTimeout;
    private readonly Dictionary<string, string> _imageIds = [];

    public string SolutionDirectory { get; }

    /// <summary>
    /// Held only while a run moves or reads the tag. Under <c>obj</c>, so git and the Docker build context
    /// never see it.
    /// </summary>
    internal string TagLockFile { get; }

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
        if (_skipBuild)
        {
            Console.WriteLine($"Skipping Docker image build ({SkipBuildVariable}=true). Reusing the images tagged {_imageTag}.");
            await FindExistingImagesAsync(images.Select(image => image.Repository).ToArray());
            return;
        }

        // Before the builds, so a run never removes the set its own builds are about to replace.
        await RemoveOldUntaggedImagesAsync();

        string cacheBust = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        foreach ((string repository, string dockerfile) in images)
        {
            _imageIds[repository] = await BuildImageAsync(repository, dockerfile, cacheBust);
        }
    }

    internal async Task<string> BuildImageAsync(string repository, string dockerfile, string cacheBust)
    {
        string imageName = ImageName(repository);
        string imageIdFile = Path.GetTempFileName();

        try
        {
            Console.WriteLine($"Building Docker image: {imageName}...");

            DockerResult result = await RunDockerAsync(
                "build", "--build-arg", $"CACHEBUST={cacheBust}", "--label", ImageLabel,
                "--iidfile", imageIdFile, "-f", dockerfile, ".");

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to build Docker image '{imageName}' (exit code {result.ExitCode}).\n" +
                    $"Dockerfile: {dockerfile}\nWorking directory: {SolutionDirectory}\n" +
                    $"Stdout:\n{result.Stdout}\nStderr:\n{result.Stderr}");
            }

            string imageId = (await File.ReadAllTextAsync(imageIdFile)).Trim();
            await TagImageAsync(imageId, imageName);
            Console.WriteLine($"Successfully built: {imageName} ({imageId})");
            return imageId;
        }
        finally
        {
            File.Delete(imageIdFile);
        }
    }

    internal string ImageName(string repository) => $"{repository}:{_imageTag}";

    public E2EWorktree(string suite)
        : this(
            suite,
            Environment.GetEnvironmentVariable(SkipBuildVariable) == "true",
            TimeSpan.FromSeconds(TagLockTimeoutSeconds))
    {
    }

    internal E2EWorktree(string suite, bool skipBuild, TimeSpan tagLockTimeout)
    {
        SolutionDirectory = FindSolutionDirectory();
        _imageTag = BuildImageTag(suite, SolutionDirectory);
        _skipBuild = skipBuild;
        _tagLockTimeout = tagLockTimeout;
        TagLockFile = Path.Combine(SolutionDirectory, "tests", "obj", $"{_imageTag}.lock");
    }

    private async Task TagImageAsync(string imageId, string imageName)
    {
        DockerResult result;

        await using (await TakeTagLockAsync())
        {
            result = await RunDockerAsync("tag", imageId, imageName);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to tag Docker image {imageId} as '{imageName}' (exit code {result.ExitCode}).\n" +
                $"Stderr:\n{result.Stderr}");
        }
    }

    private async Task<FileStream> TakeTagLockAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TagLockFile)!);
        Stopwatch waited = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                return new FileStream(TagLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when (exception.GetType() == typeof(IOException))
            {
                if (waited.Elapsed >= _tagLockTimeout)
                {
                    throw new InvalidOperationException(
                        $"Could not lock {TagLockFile} within {_tagLockTimeout.TotalSeconds:0.#} s. Another E2E run " +
                        $"in this worktree holds it while it moves or reads the tag {_imageTag}; that takes well " +
                        $"under a second, so the other run or its Docker call is probably stuck.\n" +
                        $"Last error: {exception.Message}",
                        exception);
                }

                await Task.Delay(TagLockRetryMilliseconds);
            }
        }
    }

    private async Task FindExistingImagesAsync(string[] repositories)
    {
        string[] imageNames = repositories.Select(ImageName).ToArray();
        DockerResult result;

        // Under the lock, because a tag move removes the name for a moment before it adds it again.
        await using (await TakeTagLockAsync())
        {
            result = await RunDockerAsync(["image", "inspect", "--format", "{{.Id}}", .. imageNames]);
        }

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
    /// A rebuild leaves the replaced image untagged on both stores. The one-hour age limit keeps it for a
    /// run that still starts its containers from it, in this worktree or another one. On the containerd
    /// store the hour counts from the moment the image lost its tag. On the classic store it counts from
    /// the image's build time, which a cached build keeps, so there only two overlapping runs of a suite
    /// are safe: the run that replaces an image has pruned before its build.
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

        // Windows and macOS ignore case in paths, so two ways of starting the tests can spell the same
        // worktree differently, and it must still get one tag. Linux does not, so there two paths that
        // differ only in case are two clones and must keep two tags.
        string hashedPath = OperatingSystem.IsLinux() ? solutionDirectory : solutionDirectory.ToLowerInvariant();
        string pathHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hashedPath)))[..PathHashLength];

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
