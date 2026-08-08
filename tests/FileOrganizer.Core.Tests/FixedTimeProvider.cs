namespace FileOrganizer.Core.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> stopped at one instant, so that age-based rules
/// behave the same on every machine and at every hour of the day.
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
