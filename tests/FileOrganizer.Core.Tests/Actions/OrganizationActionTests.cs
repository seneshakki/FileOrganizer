using System.Reflection;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Tests.Actions;

public sealed class OrganizationActionTests
{
    private static readonly PathPolicy Windows = new(
        PathSyntax.Windows,
        PathCaseSensitivity.Insensitive,
        PathUnicodeNormalization.FormC);

    private static readonly PathPolicy Posix = new(
        PathSyntax.Posix,
        PathCaseSensitivity.Sensitive,
        PathUnicodeNormalization.None);

    [Fact]
    public void MoveAndCopyKeepOnlyPlanningIntent()
    {
        MoveToDirectoryAction move = new(@"C:\Organized\Images");
        CopyToDirectoryAction copy = new("/archive/documents");

        Assert.Equal(OrganizationActionKind.MoveToDirectory, move.Kind);
        Assert.Equal(@"C:\Organized\Images", move.DestinationDirectory);
        Assert.True(move.IsValid(Windows));

        Assert.Equal(OrganizationActionKind.CopyToDirectory, copy.Kind);
        Assert.Equal("/archive/documents", copy.DestinationDirectory);
        Assert.True(copy.IsValid(Posix));
    }

    [Theory]
    [InlineData(null, PathProblemKind.Empty)]
    [InlineData("", PathProblemKind.Empty)]
    [InlineData("relative/directory", PathProblemKind.NotFullyQualified)]
    [InlineData(@"C:drive-relative", PathProblemKind.NotFullyQualified)]
    [InlineData(@"C:\..\escaped", PathProblemKind.TraversesAboveRoot)]
    public void InvalidMoveDestinationRemainsRepresentable(
        string? destination,
        PathProblemKind expectedPathProblem)
    {
        MoveToDirectoryAction action = new(destination);

        ActionValidationProblem problem = Assert.Single(action.Validate(Windows));

        Assert.False(action.IsValid(Windows));
        Assert.Equal(ActionValidationProblemKind.InvalidDestinationDirectory, problem.Kind);
        Assert.Equal(expectedPathProblem, problem.PathProblem.Kind);
    }

    [Fact]
    public void InvalidCopyDestinationUsesTheSameStaticContract()
    {
        CopyToDirectoryAction action = new("relative");

        ActionValidationProblem problem = Assert.Single(action.Validate(Posix));

        Assert.Equal(ActionValidationProblemKind.InvalidDestinationDirectory, problem.Kind);
        Assert.Equal(PathProblemKind.NotFullyQualified, problem.PathProblem.Kind);
    }

    [Fact]
    public void RenameAcceptsAMultipleDotLiteralWithoutInterpretingIt()
    {
        RenameToLiteralFileNameAction action = new("archive.final.tar.gz");

        Assert.Equal(OrganizationActionKind.RenameToLiteralFileName, action.Kind);
        Assert.Equal("archive.final.tar.gz", action.FileName);
        Assert.True(action.IsValid(Posix));
        Assert.Empty(action.Validate(Posix));
    }

    [Theory]
    [InlineData(null, PathProblemKind.Empty)]
    [InlineData("", PathProblemKind.Empty)]
    [InlineData(".", PathProblemKind.InvalidSingleSegment)]
    [InlineData("..", PathProblemKind.InvalidSingleSegment)]
    [InlineData("folder/name.txt", PathProblemKind.InvalidSingleSegment)]
    [InlineData("folder\\name.txt", PathProblemKind.InvalidSingleSegment)]
    [InlineData("bad\0name", PathProblemKind.ContainsNull)]
    public void InvalidRenameRemainsRepresentable(
        string? fileName,
        PathProblemKind expectedPathProblem)
    {
        RenameToLiteralFileNameAction action = new(fileName);

        ActionValidationProblem problem = Assert.Single(action.Validate(Posix));

        Assert.False(action.IsValid(Posix));
        Assert.Equal(ActionValidationProblemKind.InvalidLiteralFileName, problem.Kind);
        Assert.Equal(expectedPathProblem, problem.PathProblem.Kind);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("report.")]
    [InlineData("bad?.txt")]
    public void RenameUsesExplicitWindowsSegmentRestrictions(string fileName)
    {
        RenameToLiteralFileNameAction action = new(fileName);

        ActionValidationProblem problem = Assert.Single(action.Validate(Windows));

        Assert.Equal(ActionValidationProblemKind.InvalidLiteralFileName, problem.Kind);
        Assert.Equal(PathProblemKind.InvalidSegment, problem.PathProblem.Kind);
    }

    [Fact]
    public void ReturnedValidationProblemsCannotBeMutated()
    {
        RenameToLiteralFileNameAction action = new("../escape");
        IReadOnlyList<ActionValidationProblem> problems = action.Validate(Posix);
        IList<ActionValidationProblem> list =
            Assert.IsAssignableFrom<IList<ActionValidationProblem>>(problems);

        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Single(action.Validate(Posix));
    }

    [Fact]
    public void ActionSurfaceCannotBeExtendedOutsideCore()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(OrganizationAction).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));

        Assert.True(constructor.IsFamilyAndAssembly);
    }
}
