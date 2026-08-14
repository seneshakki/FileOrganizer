namespace FileOrganizer.Application.Watching;

/// <summary>Creates a fresh physical source for each start or resume boundary.</summary>
public interface IFileChangeSourceFactory
{
    FileChangeSourceOpenResult Open(FileChangeSourceRequest request);
}
