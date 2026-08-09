using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>
/// The narrow set of physical file-system reads used by the source scanner.
/// Kept internal so planning code can never acquire mutation capabilities.
/// </summary>
internal interface IReadOnlyFileSystemProbe
{
    PhysicalEntryObservation Observe(
        CanonicalPath path,
        CancellationToken cancellationToken = default);

    IEnumerable<string> EnumerateEntries(string directoryPath);
}
