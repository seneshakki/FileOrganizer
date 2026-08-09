using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Core.Abstractions;

/// <summary>Creates a read-only snapshot of files beneath a source root.</summary>
/// <remarks>
/// This port exposes no file-system mutation capability. Cancellation is not a
/// scan diagnostic: implementations propagate <see cref="OperationCanceledException"/>
/// and never return a partial result disguised as a completed scan.
/// </remarks>
public interface IFileScanner
{
    /// <summary>Scans the requested source scope without modifying it.</summary>
    /// <param name="sourceRoot">The canonical directory to scan.</param>
    /// <param name="recursive">Whether ordinary subdirectories are included.</param>
    /// <param name="cancellationToken">Stops the scan.</param>
    FileScanResult Scan(
        CanonicalPath sourceRoot,
        bool recursive,
        CancellationToken cancellationToken = default);
}
