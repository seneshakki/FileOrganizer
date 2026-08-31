namespace FileOrganizer.App.Services;

/// <summary>Asks the user how a dirty configuration draft should be handled.</summary>
public interface IUnsavedChangesPrompt
{
    ValueTask<UnsavedChangesDecision> PromptAsync(
        string configurationPath,
        CancellationToken cancellationToken = default);
}
