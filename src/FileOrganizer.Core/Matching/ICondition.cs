using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching;

/// <summary>
/// A question that can be asked about a file.
/// </summary>
public interface ICondition
{
    /// <summary>
    /// Asks the question about one file.
    /// </summary>
    /// <remarks>
    /// Implementations must not throw to signal that a file does not match, and
    /// must not throw because they were configured badly. Both are ordinary
    /// answers, returned as <see cref="MatchOutcome.NoMatch"/> and
    /// <see cref="MatchOutcome.Indeterminate"/>.
    /// </remarks>
    ConditionResult Evaluate(FileEntry file, EvaluationContext context);

    /// <summary>
    /// Reports everything wrong with this condition that can be found without
    /// looking at a file, such as a pattern that does not compile.
    /// </summary>
    /// <remarks>
    /// This exists so a rule editor can tell a user their rule is broken while
    /// they are writing it, instead of leaving them to notice that files are
    /// quietly not being organized.
    /// </remarks>
    IEnumerable<EvaluationProblem> Validate();
}
