using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Infrastructure.Watching;

namespace FileOrganizer.Infrastructure.Tests.Watching;

public sealed class WindowsWatchPreviewPipelineTests
{
    private static readonly WatchPreviewRuntimeOptions Options = new(
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(25),
        maxConcurrentScans: 1);

    [Fact]
    public void CreateRejectsNullInputsBeforePlatformComposition()
    {
        OrganizationConfiguration configuration = EmptyWindowsConfiguration();

        Assert.Throws<ArgumentNullException>(
            () => WindowsWatchPreviewPipeline.Create(null!, Options));
        Assert.Throws<ArgumentNullException>(
            () => WindowsWatchPreviewPipeline.Create(configuration, null!));
    }

    [Fact]
    public void CreateRejectsPathSyntaxThatWindowsAdaptersCannotObserve()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows composition validation test.");
        var configuration = new OrganizationConfiguration(
            new PathSemantics(
                PathSyntax.Posix,
                PathCaseSensitivity.Sensitive,
                PathUnicodeNormalization.None),
            []);

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => WindowsWatchPreviewPipeline.Create(configuration, Options));

        Assert.Equal("configuration", exception.ParamName);
    }

    [Fact]
    public async Task CreatesReadOnlyRuntimeThatPublishesInitialPhysicalBaseline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows physical composition test.");
        using TemporaryDirectory temporary = TemporaryDirectory.Create();
        string watchedPath = Path.Combine(temporary.FullPath, "watched");
        Directory.CreateDirectory(watchedPath);
        string sourcePath = temporary.WriteFile(
            Path.Combine("watched", "source.txt"),
            "source contents");
        var paths = new PathPolicy(
            PathSyntax.Windows,
            PathCaseSensitivity.Insensitive,
            PathUnicodeNormalization.FormC);
        CanonicalPath watchedRoot = paths.CanonicalizeAbsolute(watchedPath).Path
            ?? throw new InvalidOperationException("The temporary watch root was not canonical.");
        Guid watchRootId = Guid.NewGuid();
        var configuration = new OrganizationConfiguration(
            paths.Semantics,
            [],
            [
                new WatchRootConfiguration(
                    watchRootId,
                    watchedRoot,
                    isEnabled: true,
                    includeSubdirectories: true),
            ]);

        await using WatchPreviewRuntime runtime = WindowsWatchPreviewPipeline.Create(
            configuration,
            Options);

        Assert.Same(configuration, runtime.Configuration);
        WatchPreviewRootSnapshot beforeStart = Assert.Single(runtime.Snapshot.Roots);
        Assert.Equal(WatchRootState.RequiresInitialScan, beforeStart.State);
        Assert.Null(beforeStart.LatestPreview);

        runtime.Start();
        await runtime.DrainAsync(TestContext.Current.CancellationToken);

        WatchPreviewRootSnapshot afterBaseline = Assert.Single(runtime.Snapshot.Roots);
        Assert.Equal(watchRootId, afterBaseline.WatchRootId);
        Assert.Equal(WatchRootState.Synchronized, afterBaseline.State);
        WatchRootPreviewResult preview = Assert.IsType<WatchRootPreviewResult>(
            afterBaseline.LatestPreview);
        Assert.Equal(WatchRootPreviewOutcome.Ready, preview.Outcome);
        Assert.Equal(WatchRootPreviewTrigger.Baseline, preview.Trigger);
        Assert.Equal(
            PlanItemOutcome.Skipped,
            Assert.Single(preview.Plan!.Items).Outcome);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal("source contents", File.ReadAllText(sourcePath));
    }

    private static OrganizationConfiguration EmptyWindowsConfiguration() =>
        new(
            new PathSemantics(
                PathSyntax.Windows,
                PathCaseSensitivity.Insensitive,
                PathUnicodeNormalization.FormC),
            []);
}
