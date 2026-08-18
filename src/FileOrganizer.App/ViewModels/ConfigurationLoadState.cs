namespace FileOrganizer.App.ViewModels;

public enum ConfigurationLoadState
{
    AwaitingPath = 0,
    Loading,
    Loaded,
    Missing,
    Invalid,
    StorageFailure,
    RuntimeFailure,
}
