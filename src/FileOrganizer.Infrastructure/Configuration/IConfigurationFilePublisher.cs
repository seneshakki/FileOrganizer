namespace FileOrganizer.Infrastructure.Configuration;

internal interface IConfigurationFilePublisher
{
    void PublishNew(string temporaryPath, string destinationPath);

    void ReplaceExisting(string temporaryPath, string destinationPath);
}
