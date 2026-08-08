namespace FileOrganizer.App.ViewModels;

/// <summary>
/// State for the main window.
/// </summary>
/// <remarks>
/// Everything the window displays comes from here rather than from code-behind,
/// so what the window says can be verified without starting Avalonia. The
/// properties are read-only for now because the shell has no state that
/// changes yet; change notification arrives with the first screen that does.
/// </remarks>
public sealed class MainWindowViewModel
{
    /// <summary>Gets the application name, shown in the title bar and header.</summary>
    public string Title { get; } = "FileOrganizer";

    /// <summary>Gets a description of what the application can currently do.</summary>
    public string Summary { get; } =
        "Nothing is being watched or organized yet. Rules, dry-run previews and "
        + "automatic organization are still being built.";
}
