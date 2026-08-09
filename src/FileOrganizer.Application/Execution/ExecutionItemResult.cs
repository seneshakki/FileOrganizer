using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Execution;

/// <summary>The immutable final observation for one manifest operation.</summary>
public sealed class ExecutionItemResult
{
    private readonly ReadOnlyCollection<ExecutionProblem> _problems;

    public ExecutionItemResult(
        int ordinal,
        ExecutionItemOutcome outcome,
        IEnumerable<ExecutionProblem> problems)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        ArgumentNullException.ThrowIfNull(problems);
        ExecutionProblem[] snapshot = [.. problems];
        foreach (ExecutionProblem problem in snapshot)
        {
            ArgumentNullException.ThrowIfNull(problem, nameof(problems));
            if (problem.OperationOrdinal != ordinal)
            {
                throw new ArgumentException(
                    "Every item problem must identify that manifest operation.",
                    nameof(problems));
            }
        }

        Array.Sort(snapshot, ExecutionProblemComparer.Instance);
        ValidateProblemShape(outcome, snapshot);

        Ordinal = ordinal;
        Outcome = outcome;
        _problems = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets the corresponding manifest operation ordinal.</summary>
    public int Ordinal { get; }

    /// <summary>Gets the exact operation outcome.</summary>
    public ExecutionItemOutcome Outcome { get; }

    /// <summary>Gets structured problems in their deterministic audit order.</summary>
    public IReadOnlyList<ExecutionProblem> Problems => _problems;

    private static void ValidateProblemShape(
        ExecutionItemOutcome outcome,
        ExecutionProblem[] problems)
    {
        if (outcome is ExecutionItemOutcome.Applied or ExecutionItemOutcome.NotAttempted)
        {
            if (problems.Length != 0)
            {
                throw new ArgumentException(
                    "Applied and not-attempted operations cannot report failure problems.",
                    nameof(problems));
            }

            return;
        }

        bool allProblemsMatchOutcome = outcome switch
        {
            ExecutionItemOutcome.NotApplied => problems.All(
                problem => problem.Kind is ExecutionProblemKind.MoveFailed
                    or ExecutionProblemKind.CancellationRequested),
            ExecutionItemOutcome.Blocked => problems.All(
                problem => problem.Kind is ExecutionProblemKind.SourceRevalidationFailed
                    or ExecutionProblemKind.DestinationRevalidationFailed),
            ExecutionItemOutcome.Conflict => problems.All(
                problem => problem.Kind == ExecutionProblemKind.DestinationConflict),
            ExecutionItemOutcome.Ambiguous => problems.All(
                problem => problem.Kind is ExecutionProblemKind.MoveOutcomeAmbiguous
                    or ExecutionProblemKind.RecoveryRequired
                    or ExecutionProblemKind.JournalFailure),
            _ => false,
        };

        if (problems.Length == 0 || !allProblemsMatchOutcome)
        {
            throw new ArgumentException(
                "The operation outcome requires compatible structured problems.",
                nameof(problems));
        }
    }
}
