using System.Diagnostics;
using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Matches files by their name, without wildcards.
/// </summary>
/// <remarks>
/// Comparison is ordinal, so text is matched code unit by code unit. One
/// consequence is worth knowing: a name containing "é" written as a single
/// character does not match the same name written as "e" followed by a combining
/// accent, even though they look identical. Normalizing Unicode before comparing
/// would change which files a saved rule matches, so it is a decision to make
/// deliberately rather than a detail to slip in here.
/// </remarks>
public sealed class NameCondition : Condition
{
    private readonly NameTarget _target;
    private readonly TextOperator _operator;
    private readonly string _value;
    private readonly CaseSensitivity _caseSensitivity;

    /// <param name="target">Which part of the name to look at.</param>
    /// <param name="textOperator">How to compare.</param>
    /// <param name="value">The text to look for.</param>
    /// <param name="caseSensitivity">Whether case matters.</param>
    public NameCondition(
        NameTarget target,
        TextOperator textOperator,
        string value,
        CaseSensitivity caseSensitivity = CaseSensitivity.Insensitive)
        : base(Describe(target, textOperator, value, caseSensitivity))
    {
        _target = target;
        _operator = textOperator;
        _value = value ?? string.Empty;
        _caseSensitivity = caseSensitivity;
    }

    /// <summary>Gets the text this condition looks for.</summary>
    public string Value => _value;

    /// <summary>Gets which part of the file name this condition examines.</summary>
    public NameTarget Target => _target;

    /// <summary>Gets how the configured text is compared with the file name.</summary>
    public TextOperator Operator => _operator;

    /// <summary>Gets whether the name comparison distinguishes letter case.</summary>
    public CaseSensitivity CaseSensitivity => _caseSensitivity;

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        string name = _target == NameTarget.WithoutExtension
            ? file.NameWithoutExtension
            : file.Name;

        StringComparison comparison = _caseSensitivity.ToStringComparison();

        bool matches = _operator switch
        {
            TextOperator.Equals => name.Equals(_value, comparison),
            TextOperator.Contains => name.Contains(_value, comparison),
            TextOperator.StartsWith => name.StartsWith(_value, comparison),
            TextOperator.EndsWith => name.EndsWith(_value, comparison),

            // An unrecognised operator is rejected at construction, so reaching
            // here would mean that guard was bypassed.
            _ => throw new UnreachableException($"Unhandled text operator: {_operator}."),
        };

        return matches ? ConditionResult.Match : ConditionResult.NoMatch;
    }

    private static EvaluationProblem? Describe(
        NameTarget target,
        TextOperator textOperator,
        string value,
        CaseSensitivity caseSensitivity)
    {
        if (!Enum.IsDefined(target) || !Enum.IsDefined(textOperator) || !Enum.IsDefined(caseSensitivity))
        {
            return InvalidConfiguration("This name condition uses a setting the application does not know.");
        }

        // An empty needle would make Contains, StartsWith and EndsWith match
        // every file, which is never what someone meant to write.
        return string.IsNullOrEmpty(value)
            ? InvalidConfiguration("A name condition needs some text to look for.")
            : null;
    }
}
