using System.Collections.ObjectModel;

namespace FileOrganizer.Core.Matching.Logical;

/// <summary>
/// Base class for conditions that combine other conditions.
/// </summary>
public abstract class CompositeCondition : Condition
{
    /// <param name="children">The conditions being combined.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="children"/>, or any condition in it, is
    /// <see langword="null"/>.
    /// </exception>
    protected CompositeCondition(ICondition[] children)
        : this(Snapshot(children))
    {
    }

    private CompositeCondition(ReadOnlyCollection<ICondition> children)
        : base(DescribeProblem(children))
    {
        Children = children;
    }

    /// <summary>Gets the conditions being combined, in the order given.</summary>
    public IReadOnlyList<ICondition> Children { get; }

    /// <inheritdoc />
    public override IEnumerable<EvaluationProblem> Validate() =>
        base.Validate().Concat(Children.SelectMany(child => child.Validate()));

    /// <summary>
    /// Adds a result's problems to the running list, creating the list only if
    /// there is something to put in it.
    /// </summary>
    private protected static void Collect(
        ref List<EvaluationProblem>? problems,
        ConditionResult result)
    {
        if (result.Problems.Count == 0)
        {
            return;
        }

        problems ??= [];
        problems.AddRange(result.Problems);
    }

    private static ReadOnlyCollection<ICondition> Snapshot(ICondition[] children)
    {
        ArgumentNullException.ThrowIfNull(children);

        return Array.AsReadOnly(children.ToArray());
    }

    private static EvaluationProblem? DescribeProblem(ReadOnlyCollection<ICondition> children)
    {
        // A null child is a programming mistake rather than something a user
        // could have typed, so it is not modelled as a rule problem.

        foreach (ICondition child in children)
        {
            ArgumentNullException.ThrowIfNull(child, nameof(children));
        }

        return children.Count == 0
            ? InvalidConfiguration("A combined condition needs at least one condition to combine.")
            : null;
    }
}
