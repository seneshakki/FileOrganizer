using System.Collections.ObjectModel;
using FileOrganizer.Core.Files;

namespace FileOrganizer.Core.Matching.Conditions;

/// <summary>
/// Matches files whose extension is one of a given set.
/// </summary>
/// <remarks>
/// Entries are accepted with or without their leading dot, so <c>pdf</c> and
/// <c>.pdf</c> mean the same thing, and surrounding whitespace is trimmed.
/// Use <see cref="NoExtension"/> to match files that have none.
/// </remarks>
public sealed class ExtensionCondition : Condition
{
    /// <summary>The entry that matches files without an extension.</summary>
    public const string NoExtension = "";

    private readonly ReadOnlyCollection<string> _extensions;
    private readonly CaseSensitivity _caseSensitivity;

    /// <param name="extensions">
    /// The extensions to accept, with or without a leading dot.
    /// </param>
    /// <param name="caseSensitivity">
    /// Whether <c>.ZIP</c> counts as <c>.zip</c>. Insensitive by default,
    /// because that is what people mean by "PDF files".
    /// </param>
    public ExtensionCondition(
        IEnumerable<string> extensions,
        CaseSensitivity caseSensitivity = CaseSensitivity.Insensitive)
        : this(Materialize(extensions), caseSensitivity)
    {
    }

    private ExtensionCondition(string[] extensions, CaseSensitivity caseSensitivity)
        : base(Describe(extensions, caseSensitivity))
    {
        _extensions = Array.AsReadOnly(Array.ConvertAll(extensions, Normalize));
        _caseSensitivity = caseSensitivity;
    }

    /// <summary>Gets the extensions this condition accepts, each normalized.</summary>
    public IReadOnlyList<string> Extensions => _extensions;

    /// <inheritdoc />
    protected override ConditionResult EvaluateCore(FileEntry file, EvaluationContext context)
    {
        StringComparison comparison = _caseSensitivity.ToStringComparison();

        foreach (string extension in _extensions)
        {
            if (file.Extension.Equals(extension, comparison))
            {
                return ConditionResult.Match;
            }
        }

        return ConditionResult.NoMatch;
    }

    private static string[] Materialize(IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        return [.. extensions];
    }

    private static string Normalize(string extension)
    {
        string trimmed = extension.Trim();

        return trimmed.Length == 0 || trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    private static EvaluationProblem? Describe(string[] extensions, CaseSensitivity caseSensitivity)
    {
        if (!Enum.IsDefined(caseSensitivity))
        {
            return InvalidConfiguration("The case sensitivity setting is not one this application knows.");
        }

        if (extensions.Length == 0)
        {
            return InvalidConfiguration("An extension condition needs at least one extension to look for.");
        }

        foreach (string extension in extensions)
        {
            // A null entry is a programming mistake rather than something a
            // user could type into a rule editor.
            ArgumentNullException.ThrowIfNull(extension, nameof(extensions));

            string normalized = Normalize(extension);

            if (normalized.Length == 0 && extension.Length != 0)
            {
                return InvalidConfiguration(
                    "A blank extension entry is ambiguous. Use an empty entry to mean "
                    + "files with no extension.");
            }

            if (normalized == ".")
            {
                return InvalidConfiguration(
                    "A dot by itself is not an extension. Use an empty entry to mean "
                    + "files with no extension.");
            }

            // ".tar.gz" can never equal an extension, which is only ever the
            // part after the last dot. Saying so beats a rule that silently
            // matches nothing forever.
            if (normalized.LastIndexOf('.') > 0)
            {
                return InvalidConfiguration(
                    "An extension cannot contain a dot of its own. To match names like "
                    + "archive.tar.gz, use a glob such as *.tar.gz instead.");
            }
        }

        return null;
    }
}
