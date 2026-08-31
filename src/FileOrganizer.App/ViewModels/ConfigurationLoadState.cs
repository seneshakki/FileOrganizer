namespace FileOrganizer.App.ViewModels;

public enum ConfigurationLoadState
{
    AwaitingPath = 0,
    Loading = 1,
    Loaded = 2,
    Missing = 3,
    Invalid = 4,
    StorageFailure = 5,
    RuntimeFailure = 6,
    Editing = 7,
    Saving = 8,
    Importing = 9,
    Exporting = 10,
    Conflict = 11,
}
