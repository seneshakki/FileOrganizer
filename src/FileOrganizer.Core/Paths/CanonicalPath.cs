using System.Collections.ObjectModel;

namespace FileOrganizer.Core.Paths;

/// <summary>
/// A fully qualified path that has been parsed without consulting a file system.
/// </summary>
/// <remarks>
/// Separator and dot-segment spelling is canonical, while component case and
/// Unicode spelling are intentionally preserved. Logical equality belongs to
/// the <see cref="PathPolicy"/> that created the value; ordinary object equality
/// must not be used to decide whether two paths identify the same file.
/// </remarks>
public sealed class CanonicalPath
{
    private readonly ReadOnlyCollection<string> _comparisonSegments;
    private readonly ReadOnlyCollection<string> _segments;

    internal CanonicalPath(
        PathSemantics semantics,
        string value,
        string root,
        string comparisonRoot,
        string[] segments,
        string[] comparisonSegments,
        string comparisonValue)
    {
        Semantics = semantics;
        Value = value;
        Root = root;
        ComparisonRoot = comparisonRoot;
        ComparisonValue = comparisonValue;
        _segments = Array.AsReadOnly((string[])segments.Clone());
        _comparisonSegments = Array.AsReadOnly((string[])comparisonSegments.Clone());
    }

    /// <summary>Gets the lexical semantics under which this path was parsed.</summary>
    public PathSemantics Semantics { get; }

    /// <summary>Gets the canonical lexical path while preserving component spelling.</summary>
    public string Value { get; }

    /// <summary>Gets whether this path is its drive, share, or POSIX root.</summary>
    public bool IsRoot => _segments.Count == 0;

    /// <summary>Gets the final component, or <see langword="null"/> for a root.</summary>
    public string? FileName => IsRoot ? null : _segments[^1];

    internal string Root { get; }

    internal string ComparisonRoot { get; }

    internal string ComparisonValue { get; }

    internal IReadOnlyList<string> Segments => _segments;

    internal IReadOnlyList<string> ComparisonSegments => _comparisonSegments;

    /// <inheritdoc />
    public override string ToString() => Value;
}
