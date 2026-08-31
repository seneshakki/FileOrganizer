namespace FileOrganizer.App.Services;

/// <summary>The explicit response to a dirty configuration close request.</summary>
public enum UnsavedChangesDecision
{
    /// <summary>Keep the workspace open. This is the fail-safe default value.</summary>
    Cancel = 0,

    /// <summary>Persist the draft before closing.</summary>
    Save,

    /// <summary>Close without persisting the draft.</summary>
    Discard,
}
