namespace FileOrganizer.Core.Matching;

/// <summary>
/// Why a condition could not be decided.
/// </summary>
/// <remarks>
/// The distinction matters to whoever has to fix it. A misconfigured condition
/// is the rule author's to correct; unavailable metadata is nobody's fault and
/// editing the rule will not help.
/// </remarks>
public enum EvaluationProblemKind
{
    /// <summary>
    /// The condition was given something unusable, such as a regular
    /// expression that does not compile or a negative size.
    /// </summary>
    InvalidConfiguration = 0,

    /// <summary>
    /// A pattern took longer to run than it was allowed to, and was abandoned.
    /// </summary>
    PatternTimedOut = 1,

    /// <summary>
    /// The file metadata the condition asks about was not reported by the file
    /// system.
    /// </summary>
    MetadataUnavailable = 2,
}
