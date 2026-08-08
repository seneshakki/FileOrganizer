using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching;

/// <summary>
/// Base class for conditions, holding the one rule they all share: a condition
/// that was configured with something unusable never evaluates.
/// </summary>
/// <remarks>
/// Bad configuration is captured rather than thrown, because it arrives from
/// rules the user wrote. A regular expression with an unbalanced bracket has to
/// be representable, or a single typo would make an entire saved rule set fail
/// to load instead of showing the user which rule to fix.
/// </remarks>
public abstract class Condition : ICondition
{
    private readonly EvaluationProblem? _configurationProblem;

    /// <param name="configurationProblem">
    /// What is wrong with how this condition was configured, or
    /// <see langword="null"/> when it is usable.
    /// </param>
    protected Condition(EvaluationProblem? configurationProblem) =>
        _configurationProblem = configurationProblem;

    /// <summary>
    /// Gets whether this condition and every condition nested beneath it are
    /// usable as configured.
    /// </summary>
    public bool IsValid => !Validate().Any();

    /// <inheritdoc />
    public ConditionResult Evaluate(FileEntry file, EvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(context);

        return _configurationProblem is null
            ? EvaluateCore(file, context)
            : ConditionResult.Undetermined(_configurationProblem);
    }

    /// <inheritdoc />
    public virtual IEnumerable<EvaluationProblem> Validate() =>
        _configurationProblem is null ? [] : [_configurationProblem];

    /// <summary>
    /// Answers the question for a file. Only called when the condition is
    /// usable, so implementations need not re-check their own configuration.
    /// </summary>
    protected abstract ConditionResult EvaluateCore(FileEntry file, EvaluationContext context);

    /// <summary>Builds a problem describing unusable configuration.</summary>
    protected static EvaluationProblem InvalidConfiguration(string description) =>
        new(EvaluationProblemKind.InvalidConfiguration, description);
}
