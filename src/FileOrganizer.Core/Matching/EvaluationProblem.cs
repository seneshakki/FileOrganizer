namespace FileOrganizer.Core.Matching;

/// <summary>
/// The reason a condition could not be decided, in terms a user can act on.
/// </summary>
public sealed record EvaluationProblem
{
    /// <param name="kind">What sort of problem this is.</param>
    /// <param name="description">
    /// A description written for the person who has to fix the rule, not for a
    /// log file.
    /// </param>
    public EvaluationProblem(EvaluationProblemKind kind, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Kind = kind;
        Description = description;
    }

    /// <summary>Gets what sort of problem this is.</summary>
    public EvaluationProblemKind Kind { get; }

    /// <summary>Gets a description of the problem.</summary>
    public string Description { get; }
}
