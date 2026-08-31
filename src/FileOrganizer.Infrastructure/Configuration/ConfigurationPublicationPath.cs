namespace FileOrganizer.Infrastructure.Configuration;

internal static class ConfigurationPublicationPath
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    internal static string CreateCanonicalKey(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string canonicalPath = fullPath;
        if (OperatingSystem.IsWindows())
        {
            if (canonicalPath.StartsWith(
                    ExtendedUncPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                canonicalPath = @"\\" + canonicalPath[ExtendedUncPrefix.Length..];
            }
            else if (canonicalPath.StartsWith(
                         ExtendedPathPrefix,
                         StringComparison.OrdinalIgnoreCase))
            {
                canonicalPath = canonicalPath[ExtendedPathPrefix.Length..];
            }

            canonicalPath = canonicalPath.ToUpperInvariant();
        }

        return Path.TrimEndingDirectorySeparator(canonicalPath);
    }
}
