using System.Security;
using FileOrganizer.Core.Abstractions;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;
using FileOrganizer.Core.Scanning;

namespace FileOrganizer.Infrastructure.FileSystem;

/// <summary>Scans the host file system without modifying it.</summary>
/// <remarks>
/// Traversal is deliberately manual and non-recursive at the System.IO layer.
/// Every entry is classified through the Windows no-follow handle adapter before
/// it is treated as an ordinary file or directory. Directory enumeration itself
/// remains path based, so execution must still perform its own immediate physical
/// containment and identity revalidation.
/// </remarks>
public sealed class PhysicalFileScanner : IFileScanner
{
    private readonly PathPolicy _pathPolicy;
    private readonly IReadOnlyFileSystemProbe _probe;

    /// <summary>Creates a physical scanner using explicit lexical path semantics.</summary>
    public PhysicalFileScanner(PathPolicy pathPolicy)
        : this(RequireWindows(pathPolicy), new SystemReadOnlyFileSystemProbe())
    {
    }

    internal PhysicalFileScanner(
        PathPolicy pathPolicy,
        IReadOnlyFileSystemProbe probe)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(probe);

        PathSyntax hostSyntax = OperatingSystem.IsWindows()
            ? PathSyntax.Windows
            : PathSyntax.Posix;

        if (pathPolicy.Semantics.Syntax != hostSyntax)
        {
            throw new ArgumentException(
                "The physical scanner's path syntax must match the host operating system.",
                nameof(pathPolicy));
        }

        _pathPolicy = pathPolicy;
        _probe = probe;
    }

    /// <inheritdoc />
    public FileScanResult Scan(
        CanonicalPath sourceRoot,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);

        if (sourceRoot.Semantics != _pathPolicy.Semantics)
        {
            throw new ArgumentException(
                "The source root was canonicalized under a different path policy.",
                nameof(sourceRoot));
        }

        cancellationToken.ThrowIfCancellationRequested();

        List<FileEntry> files = [];
        List<ScanDiagnostic> diagnostics = [];

        PhysicalEntryObservation rootObservation = Observe(sourceRoot, cancellationToken);
        if (!TryAcceptDirectory(
                sourceRoot,
                rootObservation,
                isRoot: true,
                diagnostics,
                out PhysicalEntryIdentity? rootIdentity))
        {
            return Build(
                sourceRoot,
                files,
                diagnostics,
                rootPhysicalIdentity: null,
                cancellationToken);
        }

        Stack<PendingDirectory> pendingDirectories = new();
        pendingDirectories.Push(new PendingDirectory(sourceRoot, rootIdentity));

        while (pendingDirectories.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            PendingDirectory pending = pendingDirectories.Pop();
            PhysicalEntryObservation directoryObservation =
                Observe(pending.Path, cancellationToken);

            if (!TryAcceptDirectory(
                    pending.Path,
                    directoryObservation,
                    isRoot: _pathPolicy.AreEquivalent(sourceRoot, pending.Path),
                    diagnostics,
                    out PhysicalEntryIdentity? currentIdentity))
            {
                continue;
            }

            if (pending.ExpectedIdentity is not null
                && (currentIdentity is null || pending.ExpectedIdentity != currentIdentity))
            {
                diagnostics.Add(
                    new ScanDiagnostic(ScanDiagnosticKind.EntryChanged, pending.Path.Value));
                continue;
            }

            List<string> children = ReadDirectoryChildren(
                pending.Path,
                diagnostics,
                cancellationToken);

            List<PendingDirectory>? subdirectories = recursive ? [] : null;

            foreach (string rawChildPath in children)
            {
                cancellationToken.ThrowIfCancellationRequested();

                PathResolution resolution = _pathPolicy.CanonicalizeAbsolute(rawChildPath);
                if (!resolution.Succeeded)
                {
                    diagnostics.Add(
                        new ScanDiagnostic(ScanDiagnosticKind.InvalidEntryPath, rawChildPath));
                    continue;
                }

                CanonicalPath child = resolution.Path!;
                if (_pathPolicy.Relate(sourceRoot, child) != PathRelationship.Descendant)
                {
                    diagnostics.Add(
                        new ScanDiagnostic(ScanDiagnosticKind.EntryOutsideRoot, child.Value));
                    continue;
                }

                ProcessChild(
                    child,
                    Observe(child, cancellationToken),
                    files,
                    subdirectories,
                    diagnostics);
            }

            if (subdirectories is null)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            for (int index = subdirectories.Count - 1; index >= 0; index--)
            {
                pendingDirectories.Push(subdirectories[index]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Build(
            sourceRoot,
            files,
            diagnostics,
            rootIdentity,
            cancellationToken);
    }

    private PhysicalEntryObservation Observe(
        CanonicalPath path,
        CancellationToken cancellationToken)
    {
        try
        {
            PhysicalEntryObservation observation = _probe.Observe(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return observation;
        }
        catch (FileNotFoundException)
        {
            return AfterCancellationCheck(
                PhysicalEntryObservation.Missing(),
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return AfterCancellationCheck(
                PhysicalEntryObservation.Missing(),
                cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return AfterCancellationCheck(
                PhysicalEntryObservation.Inaccessible(
                    PhysicalEntryObservationReason.AccessDenied),
                cancellationToken);
        }
        catch (SecurityException)
        {
            return AfterCancellationCheck(
                PhysicalEntryObservation.Inaccessible(
                    PhysicalEntryObservationReason.AccessDenied),
                cancellationToken);
        }
        catch (IOException)
        {
            return AfterCancellationCheck(
                PhysicalEntryObservation.Inaccessible(
                    PhysicalEntryObservationReason.InputOutputFailure),
                cancellationToken);
        }
        catch (NotSupportedException)
        {
            return AfterCancellationCheck(
                PhysicalEntryObservation.Unsupported(
                    PhysicalEntryObservationReason.UnsupportedCapability),
                cancellationToken);
        }
    }

    private static PhysicalEntryObservation AfterCancellationCheck(
        PhysicalEntryObservation observation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return observation;
    }

    private static bool TryAcceptDirectory(
        CanonicalPath path,
        PhysicalEntryObservation observation,
        bool isRoot,
        List<ScanDiagnostic> diagnostics,
        out PhysicalEntryIdentity? identity)
    {
        identity = observation.Identity;

        if (observation.EntryKind == PhysicalEntryKind.Directory
            && IsPreviewableOrdinaryEntry(observation))
        {
            AddIncompleteObservationDiagnostic(path, observation, diagnostics);
            return true;
        }

        ScanDiagnosticKind kind = observation.EntryKind == PhysicalEntryKind.RegularFile
            && observation.Outcome == PhysicalEntryObservationOutcome.Success
            ? isRoot
                ? ScanDiagnosticKind.RootIsNotDirectory
                : ScanDiagnosticKind.EntryChanged
            : MapDiagnostic(observation, isRoot);

        diagnostics.Add(new ScanDiagnostic(kind, path.Value));
        return false;
    }

    private static void ProcessChild(
        CanonicalPath path,
        PhysicalEntryObservation observation,
        List<FileEntry> files,
        List<PendingDirectory>? subdirectories,
        List<ScanDiagnostic> diagnostics)
    {
        if (observation.EntryKind == PhysicalEntryKind.Directory
            && IsPreviewableOrdinaryEntry(observation))
        {
            AddIncompleteObservationDiagnostic(path, observation, diagnostics);
            subdirectories?.Add(new PendingDirectory(path, observation.Identity));
            return;
        }

        if (observation.EntryKind == PhysicalEntryKind.RegularFile
            && IsPreviewableOrdinaryEntry(observation)
            && observation.FileMetadata is PhysicalFileMetadata metadata)
        {
            AddIncompleteObservationDiagnostic(path, observation, diagnostics);
            files.Add(
                new FileEntry(
                    path,
                    metadata.Length,
                    metadata.CreatedUtc,
                    metadata.ModifiedUtc,
                    observation.Identity));
            return;
        }

        diagnostics.Add(new ScanDiagnostic(MapDiagnostic(observation, isRoot: false), path.Value));
    }

    private static bool IsPreviewableOrdinaryEntry(PhysicalEntryObservation observation) =>
        observation.Outcome == PhysicalEntryObservationOutcome.Success
        || (observation.Outcome == PhysicalEntryObservationOutcome.Unsupported
            && observation.Reason == PhysicalEntryObservationReason.UnsupportedCapability)
        || (observation.Outcome == PhysicalEntryObservationOutcome.Inaccessible
            && observation.Reason != PhysicalEntryObservationReason.EntryChanged);

    private static void AddIncompleteObservationDiagnostic(
        CanonicalPath path,
        PhysicalEntryObservation observation,
        List<ScanDiagnostic> diagnostics)
    {
        if (observation.Outcome != PhysicalEntryObservationOutcome.Inaccessible)
        {
            return;
        }

        ScanDiagnosticKind kind = MapDiagnostic(observation, isRoot: false);
        if (!diagnostics.Any(
                diagnostic => diagnostic.Kind == kind
                    && StringComparer.Ordinal.Equals(diagnostic.AffectedPath, path.Value)))
        {
            diagnostics.Add(new ScanDiagnostic(kind, path.Value));
        }
    }

    private static ScanDiagnosticKind MapDiagnostic(
        PhysicalEntryObservation observation,
        bool isRoot) => observation.Outcome switch
        {
            PhysicalEntryObservationOutcome.Missing => isRoot
                ? ScanDiagnosticKind.RootNotFound
                : ScanDiagnosticKind.EntryDisappeared,
            PhysicalEntryObservationOutcome.Inaccessible => observation.Reason switch
            {
                PhysicalEntryObservationReason.AccessDenied => ScanDiagnosticKind.AccessDenied,
                PhysicalEntryObservationReason.EntryChanged => ScanDiagnosticKind.EntryChanged,
                _ => ScanDiagnosticKind.MetadataReadFailed,
            },
            PhysicalEntryObservationOutcome.Unsupported => observation.Reason switch
            {
                PhysicalEntryObservationReason.ReparsePoint =>
                    ScanDiagnosticKind.ReparsePointSkipped,
                PhysicalEntryObservationReason.DeletePending => ScanDiagnosticKind.EntryChanged,
                PhysicalEntryObservationReason.InvalidPath => ScanDiagnosticKind.InvalidEntryPath,
                PhysicalEntryObservationReason.MetadataUnavailable =>
                    ScanDiagnosticKind.MetadataReadFailed,
                _ => ScanDiagnosticKind.UnsupportedEntrySkipped,
            },
            _ => ScanDiagnosticKind.EntryChanged,
        };

    private List<string> ReadDirectoryChildren(
        CanonicalPath directory,
        List<ScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<string> children = [];

        try
        {
            using IEnumerator<string> enumerator =
                _probe.EnumerateEntries(directory.Value).GetEnumerator();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasNext = enumerator.MoveNext();
                cancellationToken.ThrowIfCancellationRequested();

                if (!hasNext)
                {
                    break;
                }

                children.Add(enumerator.Current);
            }
        }
        catch (FileNotFoundException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.EntryDisappeared,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.EntryDisappeared,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.AccessDenied,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (SecurityException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.AccessDenied,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (IOException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.DirectoryEnumerationFailed,
                directory.Value,
                diagnostics,
                cancellationToken);
        }
        catch (NotSupportedException)
        {
            AddAfterCancellationCheck(
                ScanDiagnosticKind.DirectoryEnumerationFailed,
                directory.Value,
                diagnostics,
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return children;
    }

    private static void AddAfterCancellationCheck(
        ScanDiagnosticKind kind,
        string path,
        List<ScanDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        diagnostics.Add(new ScanDiagnostic(kind, path));
    }

    private static FileScanResult Build(
        CanonicalPath sourceRoot,
        List<FileEntry> files,
        List<ScanDiagnostic> diagnostics,
        PhysicalEntryIdentity? rootPhysicalIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileScanResult result = new(
            sourceRoot,
            files,
            diagnostics,
            rootPhysicalIdentity,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static PathPolicy RequireWindows(PathPolicy pathPolicy)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Physical scanning is Windows-only because a portable strong physical "
                + "identity adapter is not available for this host.");
        }

        return pathPolicy;
    }

    private readonly record struct PendingDirectory(
        CanonicalPath Path,
        PhysicalEntryIdentity? ExpectedIdentity);
}
