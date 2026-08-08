namespace FileOrganizer.Core.Matching;

internal static class CaseSensitivityExtensions
{
    /// <summary>
    /// Maps the policy onto a string comparison. Both branches are ordinal on
    /// purpose; see <see cref="CaseSensitivity"/>.
    /// </summary>
    public static StringComparison ToStringComparison(this CaseSensitivity caseSensitivity) =>
        caseSensitivity == CaseSensitivity.Sensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
}
