using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Watching;

/// <summary>
/// Composes the Windows read-only watched-root planning preview pipeline.
/// </summary>
public static class WindowsWatchPreviewPipeline
{
    /// <summary>
    /// Creates an unstarted multi-root preview runtime for an immutable configuration.
    /// </summary>
    /// <remarks>
    /// Construction performs no filesystem access. The returned runtime owns the
    /// physical watchers it opens after <see cref="WatchPreviewRuntime.Start"/> and
    /// must be disposed by its caller. This pipeline has no execution capability.
    /// </remarks>
    public static WatchPreviewRuntime Create(
        OrganizationConfiguration configuration,
        WatchPreviewRuntimeOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Physical watched-root previews require the Windows strong identity adapter.");
        }

        if (configuration.PathSemantics.Syntax != PathSyntax.Windows)
        {
            throw new ArgumentException(
                "The Windows watch preview pipeline requires Windows path syntax.",
                nameof(configuration));
        }

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        var pathPolicy = new PathPolicy(
            configuration.PathSemantics.Syntax,
            configuration.PathSemantics.CaseSensitivity,
            configuration.PathSemantics.UnicodeNormalization);

        return new WatchPreviewRuntime(
            configuration,
            new FileSystemWatcherChangeSourceFactory(),
            new WindowsFileStabilityObservationAdapter(),
            new PhysicalFileScanner(pathPolicy),
            new PhysicalDestinationStateInspector(),
            new OrganizationPlanner(clock),
            new TimeProviderWatchDelay(clock),
            clock,
            options);
    }
}
