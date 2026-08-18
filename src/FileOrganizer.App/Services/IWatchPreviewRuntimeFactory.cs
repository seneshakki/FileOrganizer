using FileOrganizer.Application.Configuration;
using FileOrganizer.Application.Watching;

namespace FileOrganizer.App.Services;

/// <summary>Creates the read-only watch-preview capability used by the desktop shell.</summary>
public interface IWatchPreviewRuntimeFactory
{
    IWatchPreviewRuntime Create(OrganizationConfiguration configuration);
}
