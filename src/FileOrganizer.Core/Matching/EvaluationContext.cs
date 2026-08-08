namespace FileOrganizer.Core.Matching;

/// <summary>
/// The ambient information conditions need that does not come from the file
/// itself.
/// </summary>
/// <remarks>
/// The current time is read once, when the context is created, and every
/// condition evaluated with that context sees the same instant. Reading the
/// clock per file would let "older than 30 days" mean something slightly
/// different for the first and the last file of a long run, which is the kind
/// of difference that shows up as one unexplained file left behind.
/// </remarks>
public sealed class EvaluationContext
{
    /// <param name="timeProvider">
    /// Where the current time comes from. Tests supply a fixed one so that
    /// age-based rules do not depend on when the test happens to run.
    /// </param>
    public EvaluationContext(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        UtcNow = timeProvider.GetUtcNow().ToUniversalTime();
    }

    /// <summary>Gets the instant this evaluation run is reasoning about.</summary>
    public DateTimeOffset UtcNow { get; }

    /// <summary>Creates a context that reads the machine clock.</summary>
    public static EvaluationContext Now() => new(TimeProvider.System);
}
