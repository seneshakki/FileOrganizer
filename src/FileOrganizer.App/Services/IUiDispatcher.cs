namespace FileOrganizer.App.Services;

/// <summary>Marshals presentation updates onto the owning UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
