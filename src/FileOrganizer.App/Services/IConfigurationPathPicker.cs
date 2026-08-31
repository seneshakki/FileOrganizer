namespace FileOrganizer.App.Services;

/// <summary>Selects caller-approved local paths for configuration documents.</summary>
public interface IConfigurationPathPicker
{
    ValueTask<string?> OpenExistingAsync(CancellationToken cancellationToken = default);

    ValueTask<string?> ChooseSaveDestinationAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default);
}
