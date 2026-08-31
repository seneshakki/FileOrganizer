namespace FileOrganizer.Application.Configuration;

/// <summary>Compares existing configuration paths using physical file identity.</summary>
public interface IConfigurationPathIdentityComparer
{
    ConfigurationPathIdentityComparison CompareExisting(
        string firstPath,
        string secondPath,
        CancellationToken cancellationToken = default);
}
