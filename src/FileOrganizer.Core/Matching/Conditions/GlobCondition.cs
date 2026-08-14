using System.IO.Enumeration;
using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Matches file names against a wildcard pattern such as <c>invoice-*.pdf</c>.
/// </summary>
/// <remarks>
/// The supported pattern language is deliberately small:
/// <list type="bullet">
///   <item><c>*</c> stands for any run of characters, including none.</item>
///   <item><c>?</c> stands for exactly one character.</item>
///   <item>
///     Everything else is literal, including <c>[</c>, <c>]</c>, <c>{</c>,
///     <c>}</c>, <c>+</c> and <c>.</c>. There are no character classes.
///   </item>
///   <item>
///     There is no escape character, so a name containing a literal <c>*</c> or
///     <c>?</c> cannot be matched by a glob. Use a regular expression for that.
///   </item>
///   <item>
///     There is no <c>**</c>, because a glob is matched against a file's name
///     and never against a path.
///   </item>
/// </list>
///
/// Matching is done by <see cref="FileSystemName.MatchesSimpleExpression"/>,
/// which despite its namespace is a string algorithm and touches no disk. Its
/// case folding is culture-invariant, which is the behaviour
/// <see cref="CaseSensitivity"/> calls for.
/// </remarks>
public sealed class GlobCondition : Condition
{
    private readonly string _pattern;
    private readonly NameTarget _target;
    private readonly CaseSensitivity _caseSensitivity;
    private readonly bool _ignoreCase;

    /// <param name="pattern">The wildcard pattern.</param>
    /// <param name="target">Which part of the name to match against.</param>
    /// <param name="caseSensitivity">Whether case matters.</param>
    public GlobCondition(
        string pattern,
        NameTarget target = NameTarget.FullName,
        CaseSensitivity caseSensitivity = CaseSensitivity.Insensitive)
        : base(Describe(pattern, target, caseSensitivity))
    {
        _pattern = pattern ?? string.Empty;
        _target = target;
        _caseSensitivity = caseSensitivity;
        _ignoreCase = caseSensitivity != CaseSensitivity.Sensitive;
    }

    /// <summary>Gets the pattern this condition matches against.</summary>
    public string Pattern => _pattern;

    /// <summary>Gets which part of the file name this condition examines.</summary>
    public NameTarget Target => _target;

    /// <summary>Gets whether the glob comparison distinguishes letter case.</summary>
    public CaseSensitivity CaseSensitivity => _caseSensitivity;

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        string name = _target == NameTarget.WithoutExtension
            ? file.NameWithoutExtension
            : file.Name;

        return FileSystemName.MatchesSimpleExpression(_pattern, name, _ignoreCase)
            ? ConditionResult.Match
            : ConditionResult.NoMatch;
    }

    private static EvaluationProblem? Describe(
        string pattern,
        NameTarget target,
        CaseSensitivity caseSensitivity)
    {
        if (!Enum.IsDefined(target) || !Enum.IsDefined(caseSensitivity))
        {
            return InvalidConfiguration("This glob condition uses a setting the application does not know.");
        }

        // A blank pattern matches nothing at all, not even a blank name, so a
        // rule built on one would quietly never fire.
        return string.IsNullOrWhiteSpace(pattern)
            ? InvalidConfiguration("A glob condition needs a pattern, such as *.pdf.")
            : null;
    }
}
