namespace FileOrganizer.Infrastructure.Journaling;

internal sealed class DurableJournalFilePublisher : IJournalFilePublisher
{
    public void PublishFile(string directory, string finalFileName, ReadOnlySpan<byte> content)
    {
        string tempPath = Path.Combine(directory, $".{finalFileName}.{Guid.NewGuid():N}.tmp");
        bool published = false;
        try
        {
            using (FileStream stream = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, Path.Combine(directory, finalFileName), overwrite: false);
            published = true;
        }
        finally
        {
            if (!published)
            {
                TryDeleteUnpublishedTemporaryFile(tempPath);
            }
        }
    }

    public void PublishDirectory(string stagingDirectory, string finalDirectory) =>
        Directory.Move(stagingDirectory, finalDirectory);

    private static void TryDeleteUnpublishedTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
