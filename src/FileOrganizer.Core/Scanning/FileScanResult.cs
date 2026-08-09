using System.Collections.ObjectModel;
using FileOrganizer.Core.Files;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Scanning;

/// <summary>An immutable, deterministic snapshot of one source scan.</summary>
public sealed class FileScanResult
{
    private readonly ReadOnlyCollection<ScanDiagnostic> _diagnostics;
    private readonly ReadOnlyCollection<FileEntry> _files;

    /// <param name="canonicalRoot">The canonical lexical source root.</param>
    /// <param name="files">Every regular file whose metadata was read.</param>
    /// <param name="diagnostics">Every entry that could not be accounted for.</param>
    public FileScanResult(
        CanonicalPath canonicalRoot,
        IEnumerable<FileEntry> files,
        IEnumerable<ScanDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(canonicalRoot);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(diagnostics);

        FileEntry[] fileSnapshot = [.. files];
        ScanDiagnostic[] diagnosticSnapshot = [.. diagnostics];

        foreach (FileEntry file in fileSnapshot)
        {
            ArgumentNullException.ThrowIfNull(file, nameof(files));
        }

        foreach (ScanDiagnostic diagnostic in diagnosticSnapshot)
        {
            ArgumentNullException.ThrowIfNull(diagnostic, nameof(diagnostics));
        }

        PathPolicy orderingPolicy = new(
            canonicalRoot.Semantics.Syntax,
            canonicalRoot.Semantics.CaseSensitivity,
            canonicalRoot.Semantics.UnicodeNormalization);

        CanonicalRoot = canonicalRoot;
        _files = Array.AsReadOnly(
            fileSnapshot
                .OrderBy(file => file.Path, orderingPolicy.DeterministicComparer)
                .ThenBy(file => file.SizeInBytes)
                .ThenBy(file => file.CreatedUtc)
                .ThenBy(file => file.ModifiedUtc)
                .ToArray());
        _diagnostics = Array.AsReadOnly(
            diagnosticSnapshot
                .OrderBy(diagnostic => diagnostic.AffectedPath, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Kind)
                .ToArray());
    }

    /// <summary>Gets the canonical source root.</summary>
    public CanonicalPath CanonicalRoot { get; }

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
}
