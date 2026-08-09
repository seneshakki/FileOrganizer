using System.Collections.ObjectModel;
using FileOrganizer.Core.Collections;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Physical;

namespace FileOrganizer.Core.Scanning;

/// <summary>An immutable, deterministic snapshot of one source scan.</summary>
public sealed class FileScanResult
{
    private readonly ReadOnlyCollection<ScanDiagnostic> _diagnostics;
    private readonly ReadOnlyCollection<FileEntry> _files;

    /// <param name="canonicalRoot">The canonical lexical source root.</param>
    /// <param name="files">Every regular file whose metadata was read.</param>
    /// <param name="diagnostics">Every entry that could not be accounted for.</param>
    /// <param name="cancellationToken">
    /// Stops immutable materialization and ordering without returning a partial result.
    /// </param>
    public FileScanResult(
        CanonicalPath canonicalRoot,
        IEnumerable<FileEntry> files,
        IEnumerable<ScanDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
        : this(
            canonicalRoot,
            files,
            diagnostics,
            rootPhysicalIdentity: null,
            cancellationToken)
    {
    }

    /// <summary>Creates a scan snapshot enriched with source-root identity.</summary>
    /// <param name="canonicalRoot">The canonical lexical source root.</param>
    /// <param name="files">Every regular file whose metadata was read.</param>
    /// <param name="diagnostics">Every entry that could not be accounted for.</param>
    /// <param name="rootPhysicalIdentity">
    /// Strong directory identity captured for the root, or <see langword="null"/>
    /// when only a preview-safe lexical scan was available.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops immutable materialization and ordering without returning a partial result.
    /// </param>
    public FileScanResult(
        CanonicalPath canonicalRoot,
        IEnumerable<FileEntry> files,
        IEnumerable<ScanDiagnostic> diagnostics,
        PhysicalEntryIdentity? rootPhysicalIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(canonicalRoot);
        cancellationToken.ThrowIfCancellationRequested();

        if (rootPhysicalIdentity?.EntryKind is not (null or PhysicalEntryKind.Directory))
        {
            throw new ArgumentException(
                "A scan root can retain only a directory physical identity.",
                nameof(rootPhysicalIdentity));
        }

        FileEntry[] fileSnapshot = CancellationAwareCollection.Snapshot(
            files,
            nameof(files),
            cancellationToken);
        ScanDiagnostic[] diagnosticSnapshot = CancellationAwareCollection.Snapshot(
            diagnostics,
            nameof(diagnostics),
            cancellationToken);

        PathPolicy orderingPolicy = new(
            canonicalRoot.Semantics.Syntax,
            canonicalRoot.Semantics.CaseSensitivity,
            canonicalRoot.Semantics.UnicodeNormalization);

        CancellationAwareCollection.Sort(
            fileSnapshot,
            new FileComparer(orderingPolicy),
            cancellationToken);
        CancellationAwareCollection.Sort(
            diagnosticSnapshot,
            DiagnosticComparer.Instance,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        CanonicalRoot = canonicalRoot;
        RootPhysicalIdentity = rootPhysicalIdentity;
        _files = Array.AsReadOnly(fileSnapshot);
        _diagnostics = Array.AsReadOnly(diagnosticSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Gets the canonical source root.</summary>
    public CanonicalPath CanonicalRoot { get; }

    /// <summary>
    /// Gets the path-independent identity of the scanned root directory, or
    /// <see langword="null"/> when it was not established.
    /// </summary>
    public PhysicalEntryIdentity? RootPhysicalIdentity { get; }

    /// <summary>Gets files in the path policy's deterministic total order.</summary>
    public IReadOnlyList<FileEntry> Files => _files;

    /// <summary>Gets diagnostics in deterministic path/kind order.</summary>
    public IReadOnlyList<ScanDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Gets whether the scanner accounted for the entire requested scope.
    /// </summary>
    /// <remarks>
    /// Every diagnostic currently represents an omission or uncertainty, so
    /// completeness is derived rather than accepted as a contradictory flag.
    /// </remarks>
    public bool IsComplete => _diagnostics.Count == 0;

    private sealed class FileComparer(PathPolicy policy) : IComparer<FileEntry>
    {
        public int Compare(FileEntry? left, FileEntry? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int path = policy.DeterministicComparer.Compare(left.Path, right.Path);
            if (path != 0)
            {
                return path;
            }

            int length = left.SizeInBytes.CompareTo(right.SizeInBytes);
            if (length != 0)
            {
                return length;
            }

            int created = Nullable.Compare(left.CreatedUtc, right.CreatedUtc);
            if (created != 0)
            {
                return created;
            }

            int modified = left.ModifiedUtc.CompareTo(right.ModifiedUtc);
            return modified != 0
                ? modified
                : PhysicalEntryIdentityOrdering.Compare(
                    left.PhysicalIdentity,
                    right.PhysicalIdentity);
        }
    }

    private sealed class DiagnosticComparer : IComparer<ScanDiagnostic>
    {
        public static DiagnosticComparer Instance { get; } = new();

        public int Compare(ScanDiagnostic? left, ScanDiagnostic? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int path = StringComparer.Ordinal.Compare(left.AffectedPath, right.AffectedPath);
            return path != 0 ? path : left.Kind.CompareTo(right.Kind);
        }
    }
}
