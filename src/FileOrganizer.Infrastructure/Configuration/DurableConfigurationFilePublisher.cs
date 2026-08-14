namespace FileOrganizer.Infrastructure.Configuration;

internal sealed class DurableConfigurationFilePublisher : IConfigurationFilePublisher
{
    public void PublishNew(string temporaryPath, string destinationPath) =>
        File.Move(temporaryPath, destinationPath, overwrite: false);

    public void ReplaceExisting(string temporaryPath, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Replace(
                temporaryPath,
                destinationPath,
                destinationBackupFileName: null,
                ignoreMetadataErrors: false);
            return;
        }

        // Both paths are created in the same directory. This is a rename-based
        // replacement on supported filesystems, never a cross-volume copy/delete.
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }
}
