using System.Collections.ObjectModel;

namespace FileOrganizer.Application.Execution;

/// <summary>An immutable terminal report from one execution orchestration attempt.</summary>
public sealed class ExecutionResult
{
    private readonly ReadOnlyCollection<ExecutionItemResult> _items;
    private readonly ReadOnlyCollection<ExecutionManifestProblem> _manifestProblems;
    private readonly ReadOnlyCollection<ExecutionProblem> _problems;

    public ExecutionResult(
        ExecutionManifest? manifest,
        DateTimeOffset startedAtUtc,
        DateTimeOffset finishedAtUtc,
        ExecutionOutcome outcome,
        IEnumerable<ExecutionItemResult> items,
        IEnumerable<ExecutionProblem> problems,
        IEnumerable<ExecutionManifestProblem>? manifestProblems = null,
        bool wasReplayed = false)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(problems);

        ExecutionItemResult[] itemSnapshot = [.. items];
        ExecutionProblem[] problemSnapshot = [.. problems];
        ExecutionManifestProblem[] manifestProblemSnapshot = [.. manifestProblems ?? []];

        ValidateNotNull(itemSnapshot, nameof(items));
        ValidateNotNull(problemSnapshot, nameof(problems));
        ValidateNotNull(manifestProblemSnapshot, nameof(manifestProblems));
        Array.Sort(itemSnapshot, static (left, right) => left.Ordinal.CompareTo(right.Ordinal));
        Array.Sort(problemSnapshot, ExecutionProblemComparer.Instance);
        Array.Sort(
            manifestProblemSnapshot,
            static (left, right) =>
            {
                int source = StringComparer.Ordinal.Compare(
                    left.AffectedSource?.Value,
                    right.AffectedSource?.Value);
                if (source != 0)
                {
                    return source;
                }

                int kind = left.Kind.CompareTo(right.Kind);
                if (kind != 0)
                {
                    return kind;
                }

                int action = Nullable.Compare(left.ActionKind, right.ActionKind);
                return action != 0
                    ? action
                    : StringComparer.Ordinal.Compare(left.Description, right.Description);
            });

        DateTimeOffset normalizedStart = startedAtUtc.ToUniversalTime();
        DateTimeOffset normalizedFinish = finishedAtUtc.ToUniversalTime();
        if (normalizedFinish < normalizedStart)
        {
            throw new ArgumentException(
                "The terminal result time cannot precede the execution start time.",
                nameof(finishedAtUtc));
        }

        ValidateShape(
            manifest,
            outcome,
            itemSnapshot,
            problemSnapshot,
            manifestProblemSnapshot,
            wasReplayed);

        Manifest = manifest;
        StartedAtUtc = normalizedStart;
        FinishedAtUtc = normalizedFinish;
        Outcome = outcome;
        WasReplayed = wasReplayed;
        _items = Array.AsReadOnly(itemSnapshot);
        _problems = Array.AsReadOnly(problemSnapshot);
        _manifestProblems = Array.AsReadOnly(manifestProblemSnapshot);
    }

    /// <summary>Gets the compiled request, or null when compilation was rejected.</summary>
    public ExecutionManifest? Manifest { get; }

    /// <summary>Gets the plan fingerprint, or null when no manifest was compiled.</summary>
    public PlanFingerprint? Fingerprint => Manifest?.Fingerprint;

    /// <summary>Gets the UTC time at which this orchestration attempt began.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>Gets the UTC time at which its terminal outcome was determined.</summary>
    public DateTimeOffset FinishedAtUtc { get; }

    /// <summary>Gets the explicit run-level outcome.</summary>
    public ExecutionOutcome Outcome { get; }

    /// <summary>
    /// Gets whether this completed result came from an already completed journal
    /// rather than replaying any operation.
    /// </summary>
    public bool WasReplayed { get; }

    /// <summary>Gets one explicit result per manifest item, in ordinal order.</summary>
    public IReadOnlyList<ExecutionItemResult> Items => _items;

    /// <summary>Gets run- and operation-level orchestration problems.</summary>
    public IReadOnlyList<ExecutionProblem> Problems => _problems;

    /// <summary>Gets exact compilation rejections when <see cref="Outcome"/> is Rejected.</summary>
    public IReadOnlyList<ExecutionManifestProblem> ManifestProblems => _manifestProblems;

    private static void ValidateShape(
        ExecutionManifest? manifest,
        ExecutionOutcome outcome,
        ExecutionItemResult[] items,
        ExecutionProblem[] problems,
        ExecutionManifestProblem[] manifestProblems,
        bool wasReplayed)
    {
        bool rejected = outcome == ExecutionOutcome.Rejected;
        if (rejected != (manifest is null))
        {
            throw new ArgumentException(
                "Rejected results have no manifest; every other result requires one.",
                nameof(manifest));
        }

        if (rejected != (manifestProblems.Length != 0))
        {
            throw new ArgumentException(
                "Manifest problems are required exactly for a rejected result.",
                nameof(manifestProblems));
        }

        if (rejected)
        {
            if (items.Length != 0
                || wasReplayed
                || problems.Any(problem => problem.OperationOrdinal is not null))
            {
                throw new ArgumentException(
                    "A rejected result cannot contain operation state or represent a replay.",
                    nameof(items));
            }

            return;
        }

        if (items.Length != manifest!.Items.Count)
        {
            throw new ArgumentException(
                "A terminal result must account for every manifest operation.",
                nameof(items));
        }

        for (int ordinal = 0; ordinal < items.Length; ordinal++)
        {
            if (items[ordinal].Ordinal != ordinal)
            {
                throw new ArgumentException(
                    "Operation results must contain every manifest ordinal exactly once.",
                    nameof(items));
            }
        }

        if (problems.Any(
                problem => problem.OperationOrdinal is not null
                    && problem.OperationOrdinal.Value >= manifest.Items.Count))
        {
            throw new ArgumentException(
                "An execution problem identifies an ordinal outside the manifest.",
                nameof(problems));
        }

        if (outcome == ExecutionOutcome.NoWork
            && (manifest.Items.Count != 0 || items.Length != 0))
        {
            throw new ArgumentException(
                "NoWork is valid only for an empty manifest.",
                nameof(outcome));
        }

        if (manifest.Items.Count == 0 && outcome != ExecutionOutcome.NoWork)
        {
            throw new ArgumentException(
                "An empty manifest can terminate only as NoWork.",
                nameof(outcome));
        }

        if (outcome == ExecutionOutcome.Completed
            && (items.Any(item => item.Outcome != ExecutionItemOutcome.Applied)
                || problems.Length != 0))
        {
            throw new ArgumentException(
                "Completed requires every manifest operation to be applied and no execution problems.",
                nameof(outcome));
        }

        bool anythingApplied = items.Any(item => item.Outcome == ExecutionItemOutcome.Applied);
        bool anythingAmbiguous = items.Any(
            item => item.Outcome == ExecutionItemOutcome.Ambiguous);

        ValidateProblemCorrespondence(items, problems);

        if (anythingAmbiguous && outcome != ExecutionOutcome.RecoveryRequired)
        {
            throw new ArgumentException(
                "An ambiguous operation requires a RecoveryRequired run outcome.",
                nameof(outcome));
        }

        ValidateFailStopShape(outcome, items, anythingApplied);

        if (outcome == ExecutionOutcome.RecoveryRequired
            && !anythingAmbiguous
            && !problems.Any(
                problem => problem.Kind is ExecutionProblemKind.RecoveryRequired
                    or ExecutionProblemKind.MoveOutcomeAmbiguous))
        {
            throw new ArgumentException(
                "RecoveryRequired must identify at least one ambiguous operation.",
                nameof(outcome));
        }

        if (wasReplayed && outcome != ExecutionOutcome.Completed)
        {
            throw new ArgumentException(
                "Only a completed result can represent an idempotent journal replay.",
                nameof(wasReplayed));
        }
    }

    private static void ValidateProblemCorrespondence(
        ExecutionItemResult[] items,
        ExecutionProblem[] problems)
    {
        foreach (ExecutionItemResult item in items)
        {
            if (item.Problems.Any(problem => !problems.Contains(problem)))
            {
                throw new ArgumentException(
                    "Every item problem must also appear in the run audit.",
                    nameof(problems));
            }
        }

        foreach (ExecutionProblem problem in problems.Where(
                     problem => problem.OperationOrdinal is not null))
        {
            ExecutionItemResult item = items[problem.OperationOrdinal!.Value];
            if (!item.Problems.Contains(problem))
            {
                throw new ArgumentException(
                    "Every ordinal run problem must appear on the corresponding item.",
                    nameof(problems));
            }
        }
    }

    private static void ValidateFailStopShape(
        ExecutionOutcome outcome,
        ExecutionItemResult[] items,
        bool anythingApplied)
    {
        static bool IsDefiniteStop(ExecutionItemOutcome itemOutcome) =>
            itemOutcome is ExecutionItemOutcome.NotApplied
                or ExecutionItemOutcome.Blocked
                or ExecutionItemOutcome.Conflict;

        switch (outcome)
        {
            case ExecutionOutcome.Stopped:
                bool allNotAttempted = items.All(
                    item => item.Outcome == ExecutionItemOutcome.NotAttempted);
                bool oneDefiniteStop = items.Length != 0
                    && IsDefiniteStop(items[0].Outcome)
                    && items.Skip(1).All(
                        item => item.Outcome == ExecutionItemOutcome.NotAttempted);
                if (anythingApplied || (!allNotAttempted && !oneDefiniteStop))
                {
                    throw new ArgumentException(
                        "Stopped permits no applied operation and at most one definite item failure.",
                        nameof(outcome));
                }

                break;

            case ExecutionOutcome.PartiallyCompleted:
                int partialStop = Array.FindIndex(
                    items,
                    item => item.Outcome != ExecutionItemOutcome.Applied);
                if (partialStop <= 0
                    || !IsDefiniteStop(items[partialStop].Outcome)
                    || items.Skip(partialStop + 1).Any(
                        item => item.Outcome != ExecutionItemOutcome.NotAttempted))
                {
                    throw new ArgumentException(
                        "PartiallyCompleted requires an applied prefix, one definite failure, and a not-attempted suffix.",
                        nameof(outcome));
                }

                break;

            case ExecutionOutcome.Interrupted:
                int interruption = Array.FindIndex(
                    items,
                    item => item.Outcome != ExecutionItemOutcome.Applied);
                if (interruption < 0
                    || items[interruption].Outcome is not (
                        ExecutionItemOutcome.NotApplied or ExecutionItemOutcome.NotAttempted)
                    || items.Skip(interruption + 1).Any(
                        item => item.Outcome != ExecutionItemOutcome.NotAttempted))
                {
                    throw new ArgumentException(
                        "Interrupted requires an applied prefix followed only by cancellation or not-attempted items.",
                        nameof(outcome));
                }

                break;

            case ExecutionOutcome.RecoveryRequired:
                int firstUnapplied = Array.FindIndex(
                    items,
                    item => item.Outcome != ExecutionItemOutcome.Applied);
                if (firstUnapplied < 0
                    || items.Skip(firstUnapplied).All(
                        item => item.Outcome == ExecutionItemOutcome.NotAttempted))
                {
                    break;
                }

                int ambiguous = Array.FindIndex(
                    items,
                    item => item.Outcome != ExecutionItemOutcome.Applied);
                if (ambiguous < 0
                    || items[ambiguous].Outcome != ExecutionItemOutcome.Ambiguous
                    || items.Skip(ambiguous + 1).Any(
                        item => item.Outcome != ExecutionItemOutcome.NotAttempted))
                {
                    throw new ArgumentException(
                        "RecoveryRequired requires an applied prefix, one ambiguous item, and a not-attempted suffix.",
                        nameof(outcome));
                }

                break;
        }
    }

    private static void ValidateNotNull<T>(T[] values, string parameterName)
        where T : class
    {
        foreach (T value in values)
        {
            ArgumentNullException.ThrowIfNull(value, parameterName);
        }
    }
}
