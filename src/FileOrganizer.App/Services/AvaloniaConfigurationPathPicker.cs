using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace FileOrganizer.App.Services;

/// <summary>Uses the owning Avalonia top level to select local JSON configuration files.</summary>
public sealed class AvaloniaConfigurationPathPicker : IConfigurationPathPicker
{
    private readonly TopLevel _topLevel;

    public AvaloniaConfigurationPathPicker(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        _topLevel = topLevel;
    }

    public async ValueTask<string?> OpenExistingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IStorageProvider storageProvider = _topLevel.StorageProvider;
        if (!storageProvider.CanOpen)
        {
            return null;
        }

        FilePickerFileType jsonType = CreateJsonFileType();
        IReadOnlyList<IStorageFile> selected = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open FileOrganizer configuration",
                AllowMultiple = false,
                FileTypeFilter = [jsonType],
                SuggestedFileType = jsonType,
            });
        cancellationToken.ThrowIfCancellationRequested();

        return selected.Count == 1 ? NormalizeLocalFilePath(selected[0]) : null;
    }

    public async ValueTask<string?> ChooseSaveDestinationAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        cancellationToken.ThrowIfCancellationRequested();
        IStorageProvider storageProvider = _topLevel.StorageProvider;
        if (!storageProvider.CanSave)
        {
            return null;
        }

        FilePickerFileType jsonType = CreateJsonFileType();
        IStorageFile? selected = await storageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save FileOrganizer configuration",
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "json",
                FileTypeChoices = [jsonType],
                SuggestedFileType = jsonType,
                ShowOverwritePrompt = true,
            });
        cancellationToken.ThrowIfCancellationRequested();

        return selected is null ? null : NormalizeLocalFilePath(selected);
    }

    private static FilePickerFileType CreateJsonFileType() => new("JSON configuration")
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    private static string? NormalizeLocalFilePath(IStorageFile file)
    {
        string? localPath = file.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(localPath) || !Path.IsPathFullyQualified(localPath))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(localPath);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)
                || string.Equals(
                    Path.TrimEndingDirectorySeparator(fullPath),
                    Path.TrimEndingDirectorySeparator(root),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                || string.IsNullOrEmpty(Path.GetFileName(fullPath)))
            {
                return null;
            }

            return fullPath;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return null;
        }
    }
}
