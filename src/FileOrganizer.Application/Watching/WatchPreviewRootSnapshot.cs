using FileOrganizer.Core.Paths;

namespace FileOrganizer.Application.Watching;

/// <summary>An immutable historical view of one configured watch-root pipeline.</summary>
/// <remarks>
/// A retained preview is informational and non-authoritative. This snapshot grants no execution
/// capability and does not imply that the preview remains current after publication.
/// </remarks>
public sealed class WatchPreviewRootSnapshot
{
    public WatchPreviewRootSnapshot(
        Guid watchRootId,
        CanonicalPath rootPath,
        bool isEnabled,
        bool includeSubdirectories,
        WatchRootState state,
        long revision,
        WatchDiagnostic? latestDiagnostic,
        WatchRootPreviewResult? latestPreview)
    {
        if (watchRootId == Guid.Empty)
        {
            throw new ArgumentException("A root snapshot requires a watch-root ID.", nameof(watchRootId));
        }

        ArgumentNullException.ThrowIfNull(rootPath);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (latestPreview is not null && latestPreview.WatchRootId != watchRootId)
        {
            throw new ArgumentException(
                "A retained preview must belong to the snapshot watch root.",
                nameof(latestPreview));
        }

        WatchRootId = watchRootId;
        RootPath = rootPath;
        IsEnabled = isEnabled;
        IncludeSubdirectories = includeSubdirectories;
        State = state;
        Revision = revision;
        LatestDiagnostic = latestDiagnostic;
        LatestPreview = latestPreview;
    }

    public Guid WatchRootId { get; }

    public CanonicalPath RootPath { get; }

    public bool IsEnabled { get; }

    public bool IncludeSubdirectories { get; }

    public WatchRootState State { get; }

    public long Revision { get; }

    public WatchDiagnostic? LatestDiagnostic { get; }

    /// <summary>Gets the latest historical dry-run result, never execution authorization.</summary>
    public WatchRootPreviewResult? LatestPreview { get; }
}
