using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>Combines handle-based observation with read-only directory enumeration.</summary>
internal sealed class SystemReadOnlyFileSystemProbe : IReadOnlyFileSystemProbe
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly WindowsPhysicalIdentityAdapter _identityAdapter;

    public SystemReadOnlyFileSystemProbe()
        : this(new WindowsPhysicalIdentityAdapter())
    {
    }

    internal SystemReadOnlyFileSystemProbe(
        WindowsPhysicalIdentityAdapter identityAdapter)
    {
        ArgumentNullException.ThrowIfNull(identityAdapter);
        _identityAdapter = identityAdapter;
    }

    public PhysicalEntryObservation Observe(
        CanonicalPath path,
        CancellationToken cancellationToken = default) =>
        _identityAdapter.Observe(path, cancellationToken);

    public IEnumerable<string> EnumerateEntries(string directoryPath) =>
        Directory.EnumerateFileSystemEntries(directoryPath, "*", EnumerationOptions);
}
