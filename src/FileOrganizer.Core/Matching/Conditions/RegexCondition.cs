using System.Diagnostics;
using System.Text.RegularExpressions;
using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Matches file names against a regular expression.
/// </summary>
/// <remarks>
/// A regular expression written by a user is the one part of a rule that can
/// consume unbounded time, so two defences are in place.
///
/// The pattern is first given to the non-backtracking engine, which runs in
/// time linear in the length of the name and therefore cannot be made to hang,
/// whatever the pattern. That engine does not support lookarounds,
/// backreferences or atomic groups; a pattern using those falls back to the
/// backtracking engine, where a time limit is what keeps it bounded.
///
/// If the limit is reached, the condition resolves as indeterminate and says
/// so. It does not throw: a slow pattern is a rule problem to report, not a
/// reason to take the application down.
///
/// The pattern is not anchored. Use <c>^</c> and <c>$</c> to require a whole
/// name to match.
/// </remarks>
public sealed class RegexCondition : Condition
{
    /// <summary>How long a single name is allowed to be matched for.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Regex? _regex;
    private readonly EvaluationProblem _timeoutProblem;
    private readonly NameTarget _target;

    /// <param name="pattern">The regular expression.</param>
    /// <param name="target">Which part of the name to match against.</param>
    /// <param name="caseSensitivity">Whether case matters.</param>
    /// <param name="timeout">
    /// How long one name may be matched for. Defaults to
    /// <see cref="DefaultTimeout"/>.
    /// </param>
    public RegexCondition(
        string pattern,
        NameTarget target = NameTarget.FullName,
        CaseSensitivity caseSensitivity = CaseSensitivity.Insensitive,
        TimeSpan? timeout = null)
        : this(
            Compile(pattern, target, caseSensitivity, timeout ?? DefaultTimeout),
            pattern ?? string.Empty,
            target)
    {
    }

    private RegexCondition(CompiledPattern compiled, string pattern, NameTarget target)
        : base(compiled.Problem)
    {
        _regex = compiled.Regex;
        _target = target;
        Pattern = pattern;

        _timeoutProblem = new EvaluationProblem(
            EvaluationProblemKind.PatternTimedOut,
            $"The regular expression '{pattern}' took too long to run against this file "
            + "and was abandoned. Simplify the pattern, or replace it with a glob.");
    }

    /// <summary>Gets the pattern this condition matches against.</summary>
    public string Pattern { get; }

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        Debug.Assert(_regex is not null, "A condition with no pattern never reaches evaluation.");

        string name = _target == NameTarget.WithoutExtension
            ? file.NameWithoutExtension
            : file.Name;

        try
        {
            return _regex!.IsMatch(name) ? ConditionResult.Match : ConditionResult.NoMatch;
        }
        catch (RegexMatchTimeoutException)
        {
            return ConditionResult.Undetermined(_timeoutProblem);
        }
    }

    private static CompiledPattern Compile(
        string pattern,
        NameTarget target,
        CaseSensitivity caseSensitivity,
        TimeSpan timeout)
    {
        if (!Enum.IsDefined(target) || !Enum.IsDefined(caseSensitivity))
        {
            return new CompiledPattern(
                null,
                InvalidConfiguration("This pattern condition uses a setting the application does not know."));
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return new CompiledPattern(
                null,
                InvalidConfiguration("A regular expression condition needs a pattern."));
        }

        if (timeout <= TimeSpan.Zero)
        {
            return new CompiledPattern(
                null,
                InvalidConfiguration("The time limit for a regular expression must be greater than zero."));
        }

        // CultureInvariant so that case folding does not change with the
        // machine's language; see CaseSensitivity.
        RegexOptions options = RegexOptions.CultureInvariant;

        if (caseSensitivity != CaseSensitivity.Sensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            return new CompiledPattern(
                new Regex(pattern, options | RegexOptions.NonBacktracking, timeout),
                null);
        }
        catch (NotSupportedException)
        {
            // The pattern uses a construct the linear-time engine does not have.
            // Fall through and let the time limit do the work instead.
        }
        catch (ArgumentException ex)
        {
            return new CompiledPattern(null, CannotUnderstand(ex));
        }

        try
        {
            return new CompiledPattern(new Regex(pattern, options, timeout), null);
        }
        catch (ArgumentException ex)
        {
            return new CompiledPattern(null, CannotUnderstand(ex));
        }
    }

    private static EvaluationProblem CannotUnderstand(ArgumentException exception) =>
        InvalidConfiguration(
            "This regular expression could not be understood: " + exception.Message);

    private readonly record struct CompiledPattern(Regex? Regex, EvaluationProblem? Problem);
}
