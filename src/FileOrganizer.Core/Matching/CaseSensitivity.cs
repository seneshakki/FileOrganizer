namespace FileOrganizer.Core.Matching;

/// <summary>
/// Whether text comparisons treat upper and lower case as the same.
/// </summary>
/// <remarks>
/// The policy belongs to the rule and is never inherited from the host
/// operating system. Windows compares file names case-insensitively and Linux
/// does not; a rule set that quietly followed the host would move a different
/// set of files depending on where it ran, which is the last thing anyone wants
/// from a tool that moves their files.
///
/// Both settings compare ordinally. Culture-aware comparison would make results
/// depend on the machine's language: on a Turkish system "I" does not lower-case
/// to "i", so a culture-aware rule for "Invoice" would stop matching files that
/// a user in another locale would expect it to catch.
/// </remarks>
public enum CaseSensitivity
{
    /// <summary>Upper and lower case are the same letter (ordinal).</summary>
    Insensitive = 0,

    /// <summary>Upper and lower case are different letters (ordinal).</summary>
    Sensitive = 1,
}
