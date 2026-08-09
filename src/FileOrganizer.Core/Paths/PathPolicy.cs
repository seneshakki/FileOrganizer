using System.Buffers;
using System.Text;

namespace FileOrganizer.Core.Paths;

/// <summary>
/// Parses and compares paths under explicit, host-independent lexical semantics.
/// </summary>
/// <remarks>
/// This policy performs no file-system access and cannot prove physical
/// containment through links or mount points. It is suitable for deterministic
/// planning; a future executor must add platform identity and link checks.
/// </remarks>
public sealed class PathPolicy
{
    private static readonly SearchValues<char> WindowsInvalidCharacters =
        SearchValues.Create("<>:\"|?*");

    private readonly StringComparer _componentComparer;

    public PathPolicy(
        PathSyntax syntax,
        PathCaseSensitivity caseSensitivity,
        PathUnicodeNormalization unicodeNormalization)
    {
        if (!Enum.IsDefined(syntax))
        {
            throw new ArgumentOutOfRangeException(nameof(syntax));
        }

        if (!Enum.IsDefined(caseSensitivity))
        {
            throw new ArgumentOutOfRangeException(nameof(caseSensitivity));
        }

        if (!Enum.IsDefined(unicodeNormalization))
        {
            throw new ArgumentOutOfRangeException(nameof(unicodeNormalization));
        }

        Semantics = new PathSemantics(syntax, caseSensitivity, unicodeNormalization);
        _componentComparer = caseSensitivity == PathCaseSensitivity.Sensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        LogicalComparer = new LogicalPathComparer(this);
        DeterministicComparer = new DeterministicPathComparer();
    }

    /// <summary>Gets the parsing and comparison choices made by this policy.</summary>
    public PathSemantics Semantics { get; }

    /// <summary>Gets logical equality under this policy.</summary>
    public IEqualityComparer<CanonicalPath> LogicalComparer { get; }

    /// <summary>
    /// Gets a total, culture-independent ordering. Logically equivalent paths
    /// are ordered by their preserved ordinal spelling.
    /// </summary>
    public IComparer<CanonicalPath> DeterministicComparer { get; }

    /// <summary>Parses a fully qualified path without consulting the host OS.</summary>
    public PathResolution CanonicalizeAbsolute(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Failure(PathProblemKind.Empty, "A path cannot be empty.");
        }

        if (path.Contains('\0'))
        {
            return Failure(PathProblemKind.ContainsNull, "A path cannot contain a null character.");
        }

        return Semantics.Syntax == PathSyntax.Windows
            ? CanonicalizeWindows(path)
            : CanonicalizePosix(path);
    }

    /// <summary>
    /// Appends one literal file name to a canonical directory.
    /// </summary>
    public PathResolution AppendFileName(CanonicalPath directory, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(directory);

        if (directory.Semantics != Semantics)
        {
            return Failure(
                PathProblemKind.PolicyMismatch,
                "The directory was canonicalized under a different path policy.");
        }

        PathProblem? problem = ValidateFileName(fileName);
        if (problem is not null)
        {
            return PathResolution.Failure(problem);
        }

        string separator = Semantics.Syntax == PathSyntax.Windows ? "\\" : "/";
        string value = directory.IsRoot && directory.Value.EndsWith(separator, StringComparison.Ordinal)
            ? directory.Value + fileName
            : directory.Value + separator + fileName;

        string comparisonFileName = ComparisonKey(fileName!);
        string comparisonValue = directory.IsRoot
            && directory.ComparisonValue.EndsWith(separator, StringComparison.Ordinal)
                ? directory.ComparisonValue + comparisonFileName
                : directory.ComparisonValue + separator + comparisonFileName;

        string[] segments = [.. directory.Segments, fileName!];
        string[] comparisonSegments = [.. directory.ComparisonSegments, comparisonFileName];

        return PathResolution.Success(
            new CanonicalPath(
                Semantics,
                value,
                directory.Root,
                directory.ComparisonRoot,
                segments,
                comparisonSegments,
                comparisonValue));
    }

    /// <summary>Gets the lexical parent, or <see langword="null"/> for a root.</summary>
    public CanonicalPath? GetParent(CanonicalPath path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Semantics != Semantics)
        {
            throw new ArgumentException(
                "The path was canonicalized under a different path policy.",
                nameof(path));
        }

        if (path.IsRoot)
        {
            return null;
        }

        string[] segments = path.Segments.Take(path.Segments.Count - 1).ToArray();
        string[] comparisonSegments =
            path.ComparisonSegments.Take(path.ComparisonSegments.Count - 1).ToArray();

        return BuildCanonical(
            path.Root,
            path.ComparisonRoot,
            segments,
            comparisonSegments);
    }

    /// <summary>Relates a candidate to a root using component boundaries.</summary>
    public PathRelationship Relate(CanonicalPath root, CanonicalPath candidate)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(candidate);

        if (root.Semantics != Semantics || candidate.Semantics != Semantics)
        {
            return PathRelationship.PolicyMismatch;
        }

        if (!_componentComparer.Equals(root.ComparisonRoot, candidate.ComparisonRoot))
        {
            return PathRelationship.DifferentRoot;
        }

        int sharedCount = Math.Min(root.ComparisonSegments.Count, candidate.ComparisonSegments.Count);
        for (int index = 0; index < sharedCount; index++)
        {
            if (!_componentComparer.Equals(
                    root.ComparisonSegments[index],
                    candidate.ComparisonSegments[index]))
            {
                return PathRelationship.Outside;
            }
        }

        if (candidate.ComparisonSegments.Count == root.ComparisonSegments.Count)
        {
            return PathRelationship.Same;
        }

        return candidate.ComparisonSegments.Count > root.ComparisonSegments.Count
            ? PathRelationship.Descendant
            : PathRelationship.Outside;
    }

    /// <summary>Gets whether two paths identify the same logical lexical path.</summary>
    public bool AreEquivalent(CanonicalPath left, CanonicalPath right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return LogicalComparer.Equals(left, right);
    }

    /// <summary>
    /// Validates the portable minimum for one literal file name, plus explicit
    /// restrictions of the selected syntax.
    /// </summary>
    public PathProblem? ValidateFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return Problem(PathProblemKind.Empty, "A file name cannot be empty.");
        }

        if (fileName.Contains('\0'))
        {
            return Problem(
                PathProblemKind.ContainsNull,
                "A file name cannot contain a null character.");
        }

        if (fileName is "." or ".."
            || fileName.Contains('/')
            || fileName.Contains('\\'))
        {
            return Problem(
                PathProblemKind.InvalidSingleSegment,
                "A file name must be one literal path component.");
        }

        PathProblem? unicodeProblem = ValidateUnicode(fileName);
        if (unicodeProblem is not null)
        {
            return unicodeProblem;
        }

        return Semantics.Syntax == PathSyntax.Windows
            ? ValidateWindowsSegment(fileName, isRootComponent: false)
            : null;
    }

    private PathResolution CanonicalizeWindows(string input)
    {
        string path = input.Replace('/', '\\');

        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || path.StartsWith("\\??\\", StringComparison.Ordinal))
        {
            return Failure(
                PathProblemKind.UnsupportedPathForm,
                "Windows device and extended-length paths are not supported by this policy.");
        }

        if (TryReadDriveRoot(path, out string? driveRoot, out int driveRemainderStart))
        {
            return CanonicalizeParts(
                driveRoot,
                driveRemainderStart < path.Length ? path[driveRemainderStart..] : string.Empty,
                '\\');
        }

        if (path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return CanonicalizeUnc(path);
        }

        return Failure(
            PathProblemKind.NotFullyQualified,
            "A Windows path must be drive-rooted or a complete UNC path.");
    }

    private PathResolution CanonicalizeUnc(string path)
    {
        if (path.Length > 2 && path[2] == '\\')
        {
            return Failure(
                PathProblemKind.MalformedRoot,
                "A UNC path must begin with exactly two separators.");
        }

        int serverEnd = path.IndexOf('\\', 2);
        if (serverEnd <= 2)
        {
            return Failure(
                PathProblemKind.MalformedRoot,
                "A UNC path must name both a server and a share.");
        }

        int shareStart = serverEnd + 1;
        int shareEnd = path.IndexOf('\\', shareStart);
        string server = path[2..serverEnd];
        string share = shareEnd < 0 ? path[shareStart..] : path[shareStart..shareEnd];

        if (share.Length == 0)
        {
            return Failure(
                PathProblemKind.MalformedRoot,
                "A UNC path must name both a server and a share.");
        }

        PathProblem? serverProblem = ValidateWindowsSegment(server, isRootComponent: true);
        PathProblem? shareProblem = ValidateWindowsSegment(share, isRootComponent: true);
        if (serverProblem is not null || shareProblem is not null)
        {
            return Failure(
                PathProblemKind.MalformedRoot,
                "The UNC server or share name is not valid under the Windows path policy.");
        }

        string root = $"\\\\{server}\\{share}";
        string remainder = shareEnd < 0 || shareEnd == path.Length - 1
            ? string.Empty
            : path[(shareEnd + 1)..];

        return CanonicalizeParts(root, remainder, '\\');
    }

    private PathResolution CanonicalizePosix(string path)
    {
        if (path[0] != '/')
        {
            return Failure(
                PathProblemKind.NotFullyQualified,
                "A POSIX path must begin at the slash root.");
        }

        // POSIX permits an implementation to assign special meaning to exactly
        // two leading slashes. Collapsing that spelling could make Core's
        // canonical path address a different physical location than the host.
        if (path.StartsWith("//", StringComparison.Ordinal)
            && !path.StartsWith("///", StringComparison.Ordinal))
        {
            return Failure(
                PathProblemKind.UnsupportedPathForm,
                "POSIX paths with exactly two leading slashes are not supported.");
        }

        return CanonicalizeParts("/", path[1..], '/');
    }

    private PathResolution CanonicalizeParts(string root, string remainder, char separator)
    {
        List<string> segments = [];
        List<string> comparisonSegments = [];

        foreach (string segment in remainder.Split(separator, StringSplitOptions.None))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return Failure(
                        PathProblemKind.TraversesAboveRoot,
                        "The path attempts to traverse above its root.");
                }

                segments.RemoveAt(segments.Count - 1);
                comparisonSegments.RemoveAt(comparisonSegments.Count - 1);
                continue;
            }

            PathProblem? unicodeProblem = ValidateUnicode(segment);
            if (unicodeProblem is not null)
            {
                return PathResolution.Failure(unicodeProblem);
            }

            if (Semantics.Syntax == PathSyntax.Windows)
            {
                PathProblem? segmentProblem = ValidateWindowsSegment(segment, isRootComponent: false);
                if (segmentProblem is not null)
                {
                    return PathResolution.Failure(segmentProblem);
                }
            }

            segments.Add(segment);
            comparisonSegments.Add(ComparisonKey(segment));
        }

        PathProblem? rootUnicodeProblem = ValidateUnicode(root);
        if (rootUnicodeProblem is not null)
        {
            return PathResolution.Failure(rootUnicodeProblem);
        }

        return PathResolution.Success(
            BuildCanonical(
                root,
                ComparisonKey(root),
                [.. segments],
                [.. comparisonSegments]));
    }

    private CanonicalPath BuildCanonical(
        string root,
        string comparisonRoot,
        string[] segments,
        string[] comparisonSegments)
    {
        string separator = Semantics.Syntax == PathSyntax.Windows ? "\\" : "/";
        string value = BuildValue(root, segments, separator);
        string comparisonValue = BuildValue(comparisonRoot, comparisonSegments, separator);

        return new CanonicalPath(
            Semantics,
            value,
            root,
            comparisonRoot,
            segments,
            comparisonSegments,
            comparisonValue);
    }

    private static string BuildValue(string root, string[] segments, string separator)
    {
        if (segments.Length == 0)
        {
            return root;
        }

        return root.EndsWith(separator, StringComparison.Ordinal)
            ? root + string.Join(separator, segments)
            : root + separator + string.Join(separator, segments);
    }

    private static PathProblem? ValidateWindowsSegment(string segment, bool isRootComponent)
    {
        if (segment is "." or "..")
        {
            return Problem(
                PathProblemKind.InvalidSegment,
                "A path root component cannot be a dot segment.");
        }

        if (segment.AsSpan().ContainsAny(WindowsInvalidCharacters)
            || segment.Any(character => character < ' '))
        {
            return Problem(
                PathProblemKind.InvalidSegment,
                "A Windows path component contains a reserved character.");
        }

        if (segment.EndsWith(' ') || segment.EndsWith('.'))
        {
            return Problem(
                PathProblemKind.InvalidSegment,
                "A Windows path component cannot end with a space or dot.");
        }

        if (!isRootComponent && IsReservedWindowsName(segment))
        {
            return Problem(
                PathProblemKind.InvalidSegment,
                "A Windows path component uses a reserved device name.");
        }

        return null;
    }

    private PathProblem? ValidateUnicode(string value)
    {
        if (Semantics.UnicodeNormalization != PathUnicodeNormalization.FormC)
        {
            return null;
        }

        try
        {
            _ = value.Normalize(NormalizationForm.FormC);
            return null;
        }
        catch (ArgumentException)
        {
            return Problem(
                PathProblemKind.InvalidUnicodeForNormalization,
                "The path contains malformed Unicode that cannot be normalized safely.");
        }
    }

    private string ComparisonKey(string value) =>
        Semantics.UnicodeNormalization == PathUnicodeNormalization.FormC
            ? value.Normalize(NormalizationForm.FormC)
            : value;

    private static bool TryReadDriveRoot(string path, out string root, out int remainderStart)
    {
        bool hasDriveLetter = path.Length >= 2
            && IsAsciiLetter(path[0])
            && path[1] == ':';

        if (!hasDriveLetter || path.Length < 3 || path[2] != '\\')
        {
            root = string.Empty;
            remainderStart = 0;
            return false;
        }

        root = path[..3];
        remainderStart = 3;
        return true;
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsReservedWindowsName(string segment)
    {
        string stem = segment.Split('.')[0];

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && stem[3] is >= '1' and <= '9'
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static PathProblem Problem(PathProblemKind kind, string description) =>
        new(kind, description);

    private static PathResolution Failure(PathProblemKind kind, string description) =>
        PathResolution.Failure(kind, description);

    private sealed class LogicalPathComparer(PathPolicy policy) : IEqualityComparer<CanonicalPath>
    {
        public bool Equals(CanonicalPath? left, CanonicalPath? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left is null || right is null)
            {
                return false;
            }

            return left.Semantics == policy.Semantics
                && right.Semantics == policy.Semantics
                && policy._componentComparer.Equals(left.ComparisonValue, right.ComparisonValue);
        }

        public int GetHashCode(CanonicalPath path)
        {
            ArgumentNullException.ThrowIfNull(path);

            if (path.Semantics != policy.Semantics)
            {
                return HashCode.Combine(path.Semantics, path.Value);
            }

            return policy._componentComparer.GetHashCode(path.ComparisonValue);
        }
    }

    private sealed class DeterministicPathComparer : IComparer<CanonicalPath>
    {
        public int Compare(CanonicalPath? left, CanonicalPath? right)
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

            int semanticsComparison = CompareSemantics(left.Semantics, right.Semantics);
            if (semanticsComparison != 0)
            {
                return semanticsComparison;
            }

            StringComparer logicalComparer = left.Semantics.CaseSensitivity
                == PathCaseSensitivity.Sensitive
                    ? StringComparer.Ordinal
                    : StringComparer.OrdinalIgnoreCase;

            int logicalComparison = logicalComparer.Compare(
                left.ComparisonValue,
                right.ComparisonValue);

            return logicalComparison != 0
                ? logicalComparison
                : StringComparer.Ordinal.Compare(left.Value, right.Value);
        }

        private static int CompareSemantics(PathSemantics left, PathSemantics right)
        {
            int syntax = left.Syntax.CompareTo(right.Syntax);
            if (syntax != 0)
            {
                return syntax;
            }

            int caseSensitivity = left.CaseSensitivity.CompareTo(right.CaseSensitivity);
            return caseSensitivity != 0
                ? caseSensitivity
                : left.UnicodeNormalization.CompareTo(right.UnicodeNormalization);
        }
    }

}
