namespace FileOrganizer.Infrastructure.Journaling;

internal interface IJournalFilePublisher
{
    void PublishFile(string directory, string finalFileName, ReadOnlySpan<byte> content);

    void PublishDirectory(string stagingDirectory, string finalDirectory);
}
