namespace FileOrganizer.Application.Configuration;

/// <summary>The persistence-neutral text boundary used by configuration use cases.</summary>
public interface IConfigurationTextStore
{
    ValueTask<ConfigurationTextReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken = default);

    ValueTask<ConfigurationTextWriteResult> WriteAsync(
        ConfigurationTextWriteRequest request,
        CancellationToken cancellationToken = default);
}
