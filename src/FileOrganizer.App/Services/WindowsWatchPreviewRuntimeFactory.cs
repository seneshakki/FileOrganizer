using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;
using FileOrganizer.Infrastructure.Watching;

namespace FileOrganizer.App.Services;

public sealed class WindowsWatchPreviewRuntimeFactory : IWatchPreviewRuntimeFactory
{
    private static readonly WatchPreviewRuntimeOptions DefaultOptions = new(
        debounceWindow: TimeSpan.FromMilliseconds(750),
        stabilityInterval: TimeSpan.FromSeconds(2),
        maxConcurrentScans: 2);

    public IWatchPreviewRuntime Create(OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return WindowsWatchPreviewPipeline.Create(configuration, DefaultOptions);
    }
}
