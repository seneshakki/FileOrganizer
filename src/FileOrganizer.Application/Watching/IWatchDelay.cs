namespace FileOrganizer.Application.Watching;

/// <summary>A deterministic delay boundary used by coalescing and stability checks.</summary>
/// <remarks>
/// Implementations must return control without blocking the caller and honor cancellation promptly.
/// </remarks>
public interface IWatchDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}
