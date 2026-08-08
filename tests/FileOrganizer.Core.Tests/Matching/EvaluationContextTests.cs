using FileOrganizer.Core.Matching;

namespace FileOrganizer.Core.Tests.Matching;

public sealed class EvaluationContextTests
{
    [Fact]
    public void TakesItsInstantFromTheSuppliedTimeProvider()
    {
        DateTimeOffset now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

        EvaluationContext context = new(new FixedTimeProvider(now));

        Assert.Equal(now, context.UtcNow);
    }

    [Fact]
    public void NormalizesTheInstantToUtc()
    {
        DateTimeOffset nowInAnotherOffset = new(2026, 6, 1, 15, 0, 0, TimeSpan.FromHours(3));

        EvaluationContext context = new(new FixedTimeProvider(nowInAnotherOffset));

        Assert.Equal(TimeSpan.Zero, context.UtcNow.Offset);
        Assert.Equal(12, context.UtcNow.Hour);
    }

    [Fact]
    public void ReadsTheClockOnceSoALongRunDoesNotDrift()
    {
        AdvancingTimeProvider provider = new();

        EvaluationContext context = new(provider);
        DateTimeOffset first = context.UtcNow;
        DateTimeOffset second = context.UtcNow;

        Assert.Equal(first, second);
        Assert.Equal(1, provider.ReadCount);
    }

    [Fact]
    public void RejectsAMissingTimeProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new EvaluationContext(null!));
    }

    /// <summary>A clock that moves a day forward on every read.</summary>
    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public int ReadCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            ReadCount++;
            _now = _now.AddDays(1);
            return _now;
        }
    }
}
