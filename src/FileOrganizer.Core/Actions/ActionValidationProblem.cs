using FileOrganizer.Core.Paths;

namespace FileOrganizer.Core.Actions;

/// <summary>A structured static configuration problem on an organization action.</summary>
public sealed record ActionValidationProblem
{
    public ActionValidationProblem(
        ActionValidationProblemKind kind,
        PathProblem pathProblem)
    {
        ArgumentNullException.ThrowIfNull(pathProblem);

        Kind = kind;
        PathProblem = pathProblem;
    }

    /// <summary>Gets which part of the action is invalid.</summary>
    public ActionValidationProblemKind Kind { get; }

    /// <summary>Gets the exact lexical path failure.</summary>
    public PathProblem PathProblem { get; }

    /// <summary>Gets the path failure description for preview display.</summary>
    public string Description => PathProblem.Description;
}
