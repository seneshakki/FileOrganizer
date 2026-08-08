using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Tests.Paths;

public sealed class PathPolicyTests
{
    [Fact]
    public void CanonicalizesWindowsPathsWithoutUsingHostPathRules()
    {
        PathPolicy policy = Windows();

        PathResolution result = policy.CanonicalizeAbsolute(
            @"c:/Data//Inbox/./Old/../Report.PDF/");

        Assert.True(result.Succeeded);
        Assert.Null(result.Problem);
        Assert.Equal(@"c:\Data\Inbox\Report.PDF", result.Path!.Value);
        Assert.Equal("Report.PDF", result.Path.FileName);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\Data\", @"C:\Data")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"\\server\share\folder\.\file.txt", @"\\server\share\folder\file.txt")]
    public void CanonicalizesWindowsDriveAndUncRoots(string input, string expected)
    {
        PathResolution result = Windows().CanonicalizeAbsolute(input);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Path!.Value);
    }

    [Theory]
    [InlineData("", PathProblemKind.Empty)]
    [InlineData("folder/file.txt", PathProblemKind.NotFullyQualified)]
    [InlineData(@"C:file.txt", PathProblemKind.NotFullyQualified)]
    [InlineData(@"\\server", PathProblemKind.MalformedRoot)]
    [InlineData(@"\\\server\share\file.txt", PathProblemKind.MalformedRoot)]
    [InlineData(@"\\?\C:\file.txt", PathProblemKind.UnsupportedPathForm)]
    [InlineData(@"C:\..\file.txt", PathProblemKind.TraversesAboveRoot)]
    [InlineData("C:\\Data\\bad\0name", PathProblemKind.ContainsNull)]
    public void RejectsUnsafeOrAmbiguousWindowsPaths(
        string input,
        PathProblemKind expectedProblem)
    {
        PathResolution result = Windows().CanonicalizeAbsolute(input);

        Assert.False(result.Succeeded);
        Assert.Null(result.Path);
        Assert.Equal(expectedProblem, result.Problem!.Kind);
    }

    [Theory]
    [InlineData(@"C:\Data\bad?.txt")]
    [InlineData(@"C:\Data\report.")]
    [InlineData(@"C:\Data\CON")]
    [InlineData(@"C:\Data\Lpt9.log")]
    public void RejectsExplicitWindowsSegmentHazards(string input)
    {
        PathResolution result = Windows().CanonicalizeAbsolute(input);

        Assert.False(result.Succeeded);
        Assert.Equal(PathProblemKind.InvalidSegment, result.Problem!.Kind);
    }

    [Fact]
    public void CanonicalizesPosixPathsWithoutUsingHostPathRules()
    {
        PathResolution result = Posix().CanonicalizeAbsolute(
            "/data//inbox/./old/../archive.tar.gz/");

        Assert.True(result.Succeeded);
        Assert.Equal("/data/inbox/archive.tar.gz", result.Path!.Value);
        Assert.Equal("archive.tar.gz", result.Path.FileName);
    }

    [Fact]
    public void PosixPolicyDoesNotSilentlyInheritWindowsReservedNames()
    {
        PathResolution result = Posix().CanonicalizeAbsolute("/data/CON");

        Assert.True(result.Succeeded);
        Assert.Equal("CON", result.Path!.FileName);
    }

    [Theory]
    [InlineData("data/file.txt", PathProblemKind.NotFullyQualified)]
    [InlineData("/../file.txt", PathProblemKind.TraversesAboveRoot)]
    [InlineData("/data/bad\0name", PathProblemKind.ContainsNull)]
    public void RejectsUnsafePosixPaths(string input, PathProblemKind expectedProblem)
    {
        PathResolution result = Posix().CanonicalizeAbsolute(input);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedProblem, result.Problem!.Kind);
    }

    [Fact]
    public void RelatesPathsByComponentsRatherThanStringPrefixes()
    {
        PathPolicy policy = Windows(PathCaseSensitivity.Insensitive);
        CanonicalPath root = Canonical(policy, @"C:\Data");

        Assert.Equal(PathRelationship.Same, policy.Relate(root, Canonical(policy, @"c:\DATA\")));
        Assert.Equal(
            PathRelationship.Descendant,
            policy.Relate(root, Canonical(policy, @"C:\Data\Inbox\report.pdf")));
        Assert.Equal(
            PathRelationship.Outside,
            policy.Relate(root, Canonical(policy, @"C:\Database\report.pdf")));
        Assert.Equal(
            PathRelationship.DifferentRoot,
            policy.Relate(root, Canonical(policy, @"D:\Data\report.pdf")));
    }

    [Fact]
    public void DotSegmentsCannotHideContainmentEscape()
    {
        PathPolicy policy = Windows();
        CanonicalPath root = Canonical(policy, @"C:\Data");
        CanonicalPath candidate = Canonical(policy, @"C:\Data\Inbox\..\..\Secret\file.txt");

        Assert.Equal(@"C:\Secret\file.txt", candidate.Value);
        Assert.Equal(PathRelationship.Outside, policy.Relate(root, candidate));
    }

    [Fact]
    public void CaseEquivalenceIsExplicit()
    {
        PathPolicy sensitive = Windows(PathCaseSensitivity.Sensitive);
        PathPolicy insensitive = Windows(PathCaseSensitivity.Insensitive);

        Assert.False(
            sensitive.AreEquivalent(
                Canonical(sensitive, @"C:\Data\Report.txt"),
                Canonical(sensitive, @"c:\data\report.txt")));
        Assert.True(
            insensitive.AreEquivalent(
                Canonical(insensitive, @"C:\Data\Report.txt"),
                Canonical(insensitive, @"c:\data\report.txt")));
    }

    [Fact]
    public void UnicodeEquivalenceIsExplicitAndPreservesPhysicalSpelling()
    {
        const string composed = "caf\u00e9.txt";
        const string decomposed = "cafe\u0301.txt";
        PathPolicy exact = Posix(unicode: PathUnicodeNormalization.None);
        PathPolicy normalized = Posix(unicode: PathUnicodeNormalization.FormC);

        Assert.False(
            exact.AreEquivalent(
                Canonical(exact, "/data/" + composed),
                Canonical(exact, "/data/" + decomposed)));

        CanonicalPath composedPath = Canonical(normalized, "/data/" + composed);
        CanonicalPath decomposedPath = Canonical(normalized, "/data/" + decomposed);

        Assert.True(normalized.AreEquivalent(composedPath, decomposedPath));
        Assert.Equal("/data/" + composed, composedPath.Value);
        Assert.Equal("/data/" + decomposed, decomposedPath.Value);
    }

    [Fact]
    public void DeterministicComparerUsesOrdinalSpellingAsLogicalTieBreaker()
    {
        PathPolicy policy = Windows(PathCaseSensitivity.Insensitive);
        CanonicalPath first = Canonical(policy, @"C:\Data\a.txt");
        CanonicalPath second = Canonical(policy, @"c:\data\A.txt");

        Assert.True(policy.AreEquivalent(first, second));

        CanonicalPath[] forward = [first, second];
        CanonicalPath[] reverse = [second, first];
        Array.Sort(forward, policy.DeterministicComparer);
        Array.Sort(reverse, policy.DeterministicComparer);

        Assert.Equal(forward.Select(path => path.Value), reverse.Select(path => path.Value));
        Assert.NotEqual(0, policy.DeterministicComparer.Compare(first, second));
    }

    [Fact]
    public void AppendsOneLiteralNameAndGetsItsParent()
    {
        PathPolicy policy = Posix();
        CanonicalPath directory = Canonical(policy, "/data/inbox");

        PathResolution appended = policy.AppendFileName(directory, "archive.final.tar.gz");

        Assert.True(appended.Succeeded);
        Assert.Equal("/data/inbox/archive.final.tar.gz", appended.Path!.Value);
        Assert.Equal(directory.Value, policy.GetParent(appended.Path)!.Value);
        Assert.True(policy.AreEquivalent(directory, policy.GetParent(appended.Path)!));
        Assert.Null(policy.GetParent(Canonical(policy, "/")));
    }

    [Theory]
    [InlineData(null, PathProblemKind.Empty)]
    [InlineData("", PathProblemKind.Empty)]
    [InlineData(".", PathProblemKind.InvalidSingleSegment)]
    [InlineData("..", PathProblemKind.InvalidSingleSegment)]
    [InlineData("nested/name.txt", PathProblemKind.InvalidSingleSegment)]
    [InlineData("nested\\name.txt", PathProblemKind.InvalidSingleSegment)]
    [InlineData("bad\0name", PathProblemKind.ContainsNull)]
    public void RejectsNamesThatAreNotOnePortableLiteralSegment(
        string? fileName,
        PathProblemKind expectedProblem)
    {
        PathPolicy policy = Posix();

        PathResolution result = policy.AppendFileName(Canonical(policy, "/data"), fileName);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedProblem, result.Problem!.Kind);
    }

    [Fact]
    public void ReportsPolicyMismatchRatherThanCombiningIncompatiblePaths()
    {
        PathPolicy windows = Windows();
        PathPolicy posix = Posix();
        CanonicalPath posixDirectory = Canonical(posix, "/data");

        PathResolution append = windows.AppendFileName(posixDirectory, "file.txt");

        Assert.False(append.Succeeded);
        Assert.Equal(PathProblemKind.PolicyMismatch, append.Problem!.Kind);
        Assert.Equal(
            PathRelationship.PolicyMismatch,
            windows.Relate(Canonical(windows, @"C:\Data"), posixDirectory));
    }

    private static PathPolicy Windows(
        PathCaseSensitivity caseSensitivity = PathCaseSensitivity.Sensitive,
        PathUnicodeNormalization unicode = PathUnicodeNormalization.None) =>
        new(PathSyntax.Windows, caseSensitivity, unicode);

    private static PathPolicy Posix(
        PathCaseSensitivity caseSensitivity = PathCaseSensitivity.Sensitive,
        PathUnicodeNormalization unicode = PathUnicodeNormalization.None) =>
        new(PathSyntax.Posix, caseSensitivity, unicode);

    private static CanonicalPath Canonical(PathPolicy policy, string value)
    {
        PathResolution result = policy.CanonicalizeAbsolute(value);
        Assert.True(result.Succeeded, result.Problem?.Description);
        return result.Path!;
    }
}
