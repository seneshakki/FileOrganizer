using System.Collections.ObjectModel;
using FileOrganizer.Application.Execution;
using FileOrganizer.Application.Recovery;

namespace FileOrganizer.Application.Orchestration;

/// <summary>An immutable report from recovery, which never invokes a move.</summary>
public sealed class ExecutionRecoveryResult
{
    private readonly ReadOnlyCollection<ExecutionProblem> _problems;

    public ExecutionRecoveryResult(
        PlanFingerprint fingerprint,
        ExecutionRecoveryOutcome outcome,
        ExecutionManifest? manifest = null,
        RecoveryResolution? resolution = null,
        int? nextOperationOrdinal = null,
        bool journalWasUpdated = false,
        IEnumerable<ExecutionProblem>? problems = null)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (nextOperationOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(nextOperationOrdinal));
        }

        ExecutionProblem[] problemSnapshot = [.. problems ?? []];
        foreach (ExecutionProblem problem in problemSnapshot)
        {
            ArgumentNullException.ThrowIfNull(problem, nameof(problems));
        }

        Array.Sort(
            problemSnapshot,
            static (left, right) =>
            {
                int ordinal = Nullable.Compare(left.OperationOrdinal, right.OperationOrdinal);
                if (ordinal != 0)
                {
                    return ordinal;
                }

                int kind = left.Kind.CompareTo(right.Kind);
                return kind != 0
                    ? kind
                    : StringComparer.Ordinal.Compare(left.Description, right.Description);
            });

        if (outcome == ExecutionRecoveryOutcome.NoJournal && manifest is not null)
        {
            throw new ArgumentException(
                "A missing journal cannot expose a manifest.",
                nameof(manifest));
        }

        ValidateShape(
            fingerprint,
            outcome,
            manifest,
            resolution,
            nextOperationOrdinal,
            journalWasUpdated,
            problemSnapshot);

        Fingerprint = fingerprint;
        Outcome = outcome;
        Manifest = manifest;
        Resolution = resolution;
        NextOperationOrdinal = nextOperationOrdinal;
        JournalWasUpdated = journalWasUpdated;
        _problems = Array.AsReadOnly(problemSnapshot);
    }

    public PlanFingerprint Fingerprint { get; }

    public ExecutionRecoveryOutcome Outcome { get; }

    public ExecutionManifest? Manifest { get; }

    public RecoveryResolution? Resolution { get; }

    /// <summary>Gets the operation a future explicit resume would reconsider.</summary>
    public int? NextOperationOrdinal { get; }

    public bool JournalWasUpdated { get; }

    public IReadOnlyList<ExecutionProblem> Problems => _problems;

    private static void ValidateShape(
        PlanFingerprint fingerprint,
        ExecutionRecoveryOutcome outcome,
        ExecutionManifest? manifest,
        RecoveryResolution? resolution,
        int? nextOperationOrdinal,
        bool journalWasUpdated,
        ExecutionProblem[] problems)
    {
        if (manifest is not null && !manifest.Fingerprint.Equals(fingerprint))
        {
            throw new ArgumentException(
                "A recovery manifest must match the requested fingerprint.",
                nameof(manifest));
        }

        if (nextOperationOrdinal is int ordinal
            && (manifest is null || ordinal >= manifest.Items.Count))
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextOperationOrdinal),
                "A recovery ordinal must identify an item in the durable manifest.");
        }

        if (problems.Any(
                problem => problem.OperationOrdinal is int problemOrdinal
                    && (manifest is null || problemOrdinal >= manifest.Items.Count)))
        {
            throw new ArgumentException(
                "A recovery problem identifies an item outside the durable manifest.",
                nameof(problems));
        }

        switch (outcome)
        {
            case ExecutionRecoveryOutcome.NoJournal:
                RequireEmptyState(
                    manifest,
                    resolution,
                    nextOperationOrdinal,
                    journalWasUpdated,
                    problems,
                    nameof(outcome));
                break;

            case ExecutionRecoveryOutcome.Busy:
                if (manifest is not null
                    || resolution is not null
                    || nextOperationOrdinal is not null
                    || journalWasUpdated
                    || problems.Length == 0
                    || problems.Any(
                        problem => problem.Kind
                            != ExecutionProblemKind.ExecutionAlreadyInProgress))
                {
                    throw new ArgumentException(
                        "Busy recovery has no journal state and requires an execution-in-progress problem.",
                        nameof(outcome));
                }

                break;

            case ExecutionRecoveryOutcome.Completed:
                if (manifest is null
                    || resolution is not null
                    || nextOperationOrdinal is not null
                    || problems.Length != 0)
                {
                    throw new ArgumentException(
                        "Completed recovery requires only a durable manifest.",
                        nameof(outcome));
                }

                break;

            case ExecutionRecoveryOutcome.Resumable:
                if (manifest is null
                    || resolution?.Disposition is not (
                        null
                        or RecoveryDisposition.Applied
                        or RecoveryDisposition.NotApplied)
                    || problems.Length != 0)
                {
                    throw new ArgumentException(
                        "Resumable recovery requires a manifest and at most an applied/not-applied resolution.",
                        nameof(outcome));
                }

                break;

            case ExecutionRecoveryOutcome.Blocked:
                RequireManifestAndProblem(
                    manifest,
                    resolution,
                    nextOperationOrdinal,
                    problems,
                    [
                        ExecutionProblemKind.SourceRevalidationFailed,
                        ExecutionProblemKind.DestinationRevalidationFailed,
                    ],
                    expectedResolution: null,
                    nameof(outcome));
                if (journalWasUpdated)
                {
                    throw new ArgumentException(
                        "A replayed pre-intent block is not a recovery publication.",
                        nameof(journalWasUpdated));
                }

                break;

            case ExecutionRecoveryOutcome.Conflict:
                RequireManifestAndProblem(
                    manifest,
                    resolution,
                    nextOperationOrdinal,
                    problems,
                    [ExecutionProblemKind.DestinationConflict],
                    RecoveryDisposition.Conflict,
                    nameof(outcome));
                break;

            case ExecutionRecoveryOutcome.Ambiguous:
                RequireManifestAndProblem(
                    manifest,
                    resolution,
                    nextOperationOrdinal,
                    problems,
                    [ExecutionProblemKind.MoveOutcomeAmbiguous],
                    RecoveryDisposition.Ambiguous,
                    nameof(outcome));
                break;

            case ExecutionRecoveryOutcome.ManualRecoveryRequired:
                RequireManifestAndProblem(
                    manifest,
                    resolution,
                    nextOperationOrdinal,
                    problems,
                    [ExecutionProblemKind.RecoveryRequired],
                    RecoveryDisposition.ManualRecovery,
                    nameof(outcome));
                break;

            case ExecutionRecoveryOutcome.Interrupted:
                if (manifest is null
                    || resolution is not null
                    || journalWasUpdated
                    || problems.Length == 0
                    || problems.Any(
                        problem => problem.Kind != ExecutionProblemKind.JournalFailure))
                {
                    throw new ArgumentException(
                        "Interrupted recovery requires a manifest and a non-resumable journal failure.",
                        nameof(outcome));
                }

                break;

            case ExecutionRecoveryOutcome.InvalidJournal:
                if (resolution is not null
                    || nextOperationOrdinal is not null
                    || journalWasUpdated
                    || problems.Length == 0
                    || problems.Any(
                        problem => problem.Kind != ExecutionProblemKind.InvalidJournal))
                {
                    throw new ArgumentException(
                        "Invalid-journal recovery requires only an invalid-journal problem.",
                        nameof(outcome));
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    private static void RequireEmptyState(
        ExecutionManifest? manifest,
        RecoveryResolution? resolution,
        int? nextOperationOrdinal,
        bool journalWasUpdated,
        ExecutionProblem[] problems,
        string parameterName)
    {
        if (manifest is not null
            || resolution is not null
            || nextOperationOrdinal is not null
            || journalWasUpdated
            || problems.Length != 0)
        {
            throw new ArgumentException(
                "This recovery outcome cannot contain durable journal state.",
                parameterName);
        }
    }

    private static void RequireManifestAndProblem(
        ExecutionManifest? manifest,
        RecoveryResolution? resolution,
        int? affectedOrdinal,
        ExecutionProblem[] problems,
        IReadOnlyCollection<ExecutionProblemKind> allowedProblemKinds,
        RecoveryDisposition? expectedResolution,
        string parameterName)
    {
        bool resolutionMatches = resolution is null
            || (expectedResolution.HasValue
                && resolution.Disposition == expectedResolution.Value);
        if (manifest is null
            || !resolutionMatches
            || affectedOrdinal is null
            || problems.Length == 0
            || problems.Any(problem => !allowedProblemKinds.Contains(problem.Kind))
            || problems.Any(
                problem => problem.OperationOrdinal is int ordinal
                    && ordinal != affectedOrdinal.Value))
        {
            throw new ArgumentException(
                "The recovery outcome has contradictory manifest, resolution, or problem state.",
                parameterName);
        }
    }
}
