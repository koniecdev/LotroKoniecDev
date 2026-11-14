using System.Collections.Concurrent;
using System.Diagnostics;
using LotroKoniecDev.Tests.Shared;
using Shouldly;

namespace LotroKoniecDev.TranslationSystem.E2E.Tests.Tests;

/// <summary>
/// How <see cref="E2EWorktree"/> moves the worktree tag (#889), on a tiny probe image. It joins no
/// collection, so it never starts the stack, but it needs Docker. Each test has its own suite name, so its
/// tag and its lock file are its own.
/// </summary>
public sealed class E2EWorktreeTests : IAsyncLifetime
{
    private const string ProbeRepository = "lotrokoniecdev-tag-probe";
    private const string ProbeDockerfile = "tests/LotroKoniecDev.TranslationSystem.E2E.Tests/Probes/tag-probe.dockerfile";

    private static readonly TimeSpan DefaultTagLockTimeout = TimeSpan.FromMinutes(1);

    private readonly string _suite = $"selftest-{Guid.NewGuid():N}"[..17];
    private readonly ConcurrentBag<string> _builtImageIds = [];

    [Fact]
    public async Task BuildImageAsync_SecondBuildUnderTheSameTag_KeepsTheFirstImage()
    {
        E2EWorktree worktree = NewWorktree(DefaultTagLockTimeout, skipBuild: false);
        string firstImageId = await BuildProbeAsync(worktree);
        string secondImageId = await BuildProbeAsync(worktree);
        secondImageId.ShouldNotBe(firstImageId);

        bool firstImageExists = await ImageExistsAsync(firstImageId);

        firstImageExists.ShouldBeTrue();
    }

    [Fact]
    public async Task BuildImageAsync_TwoRunsBuildAtOnce_BothImagesExistAndTheTagNamesOne()
    {
        E2EWorktree firstRun = NewWorktree(DefaultTagLockTimeout, skipBuild: false);
        E2EWorktree secondRun = NewWorktree(DefaultTagLockTimeout, skipBuild: false);

        string[] imageIds = await Task.WhenAll(BuildProbeAsync(firstRun), BuildProbeAsync(secondRun));

        (await ImageExistsAsync(imageIds[0])).ShouldBeTrue();
        (await ImageExistsAsync(imageIds[1])).ShouldBeTrue();
        imageIds.ShouldContain(await TaggedImageIdAsync(firstRun.ImageName(ProbeRepository)));
    }

    [Fact]
    public async Task BuildImageAsync_TagLockHeldByAnotherRun_ThrowsNamingTheLockFileAndLeavesTheTag()
    {
        E2EWorktree worktree = NewWorktree(TimeSpan.FromMilliseconds(300), skipBuild: false);
        string taggedImageId = await BuildProbeAsync(worktree);
        InvalidOperationException exception;

        await using (new FileStream(worktree.TagLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            exception = await Should.ThrowAsync<InvalidOperationException>(() => BuildProbeAsync(worktree));
        }

        exception.Message.ShouldContain(worktree.TagLockFile);
        (await TaggedImageIdAsync(worktree.ImageName(ProbeRepository))).ShouldBe(taggedImageId);
    }

    [Fact]
    public async Task BuildImageAsync_RepositoryNameDockerRefusesToTag_ThrowsTheTagFailure()
    {
        E2EWorktree worktree = NewWorktree(DefaultTagLockTimeout, skipBuild: false);

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => worktree.BuildImageAsync("LotroKoniecDev-Tag-Probe", ProbeDockerfile, NewCacheBust()));

        exception.Message.ShouldStartWith("Failed to tag Docker image");
    }

    [Fact]
    public async Task BuildImagesAsync_SkipBuildAfterABuild_TakesTheImageTheBuildTagged()
    {
        string builtImageId = await BuildProbeAsync(NewWorktree(DefaultTagLockTimeout, skipBuild: false));
        E2EWorktree skippingRun = NewWorktree(DefaultTagLockTimeout, skipBuild: true);

        await skippingRun.BuildImagesAsync([(ProbeRepository, ProbeDockerfile)]);

        skippingRun.Image(ProbeRepository).ShouldBe(builtImageId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        E2EWorktree worktree = NewWorktree(DefaultTagLockTimeout, skipBuild: false);
        await DockerAsync(["image", "rm", "--force", worktree.ImageName(ProbeRepository), .. _builtImageIds]);

        // File.Delete throws when the folder is missing, and a test that fails before its first tag
        // move never creates it.
        if (File.Exists(worktree.TagLockFile))
        {
            File.Delete(worktree.TagLockFile);
        }
    }

    private E2EWorktree NewWorktree(TimeSpan tagLockTimeout, bool skipBuild) => new(_suite, skipBuild, tagLockTimeout);

    private async Task<string> BuildProbeAsync(E2EWorktree worktree)
    {
        string imageId = await worktree.BuildImageAsync(ProbeRepository, ProbeDockerfile, NewCacheBust());
        _builtImageIds.Add(imageId);
        return imageId;
    }

    private static string NewCacheBust() => Guid.NewGuid().ToString("N");

    private static async Task<bool> ImageExistsAsync(string imageId) =>
        (await DockerAsync("image", "inspect", imageId)).ExitCode == 0;

    private static async Task<string> TaggedImageIdAsync(string imageName) =>
        (await DockerAsync("image", "inspect", "--format", "{{.Id}}", imageName)).Stdout.Trim();

    private static async Task<(int ExitCode, string Stdout)> DockerAsync(params string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "docker",
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
        await stderrTask;

        return (process.ExitCode, await stdoutTask);
    }
}
