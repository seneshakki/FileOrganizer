using FileOrganizer.Application.Watching;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Infrastructure.FileSystem;

namespace FileOrganizer.Infrastructure.Watching;

/// <summary>Delegates read-only stability observations to the Windows identity adapter.</summary>
public sealed class WindowsFileStabilityObservationAdapter : IFileStabilityObservationPort
{
    private readonly WindowsPhysicalIdentityAdapter _identityAdapter;

    public WindowsFileStabilityObservationAdapter()
        : this(new WindowsPhysicalIdentityAdapter())
    {
    }

    internal WindowsFileStabilityObservationAdapter(
        WindowsPhysicalIdentityAdapter identityAdapter)
    {
        ArgumentNullException.ThrowIfNull(identityAdapter);
        _identityAdapter = identityAdapter;
    }

    public ValueTask<PhysicalEntryObservation> ObserveAsync(
        CanonicalPath path,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_identityAdapter.Observe(path, cancellationToken));
}
