namespace FileOrganizer.App.Startup;

/// <summary>Parses the deliberately narrow desktop configuration argument surface.</summary>
public sealed class ConfigurationPathArguments
{
    private ConfigurationPathArguments(string? configurationPath, string? problem)
    {
        ConfigurationPath = configurationPath;
        Problem = problem;
    }

    public string? ConfigurationPath { get; }

    public string? Problem { get; }

    public bool IsValid => ConfigurationPath is not null && Problem is null;

    public static ConfigurationPathArguments Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string[] values = [.. arguments];
        if (values.Length == 0)
        {
            return Invalid("Start FileOrganizer with --config followed by an absolute JSON file path.");
        }

        if (values.Length != 2 || !string.Equals(values[0], "--config", StringComparison.Ordinal))
        {
            return Invalid("The only supported startup syntax is --config <absolute JSON file path>.");
        }

        string path = values[1];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return Invalid("The --config value must be an absolute file path.");
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)
                || string.Equals(
                    Path.TrimEndingDirectorySeparator(fullPath),
                    Path.TrimEndingDirectorySeparator(root),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal)
                || string.IsNullOrEmpty(Path.GetFileName(fullPath)))
            {
                return Invalid("The --config value must identify a file, not a filesystem root.");
            }

            return new ConfigurationPathArguments(fullPath, null);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return Invalid("The --config value is not a valid absolute file path.");
        }
    }

    private static ConfigurationPathArguments Invalid(string problem) => new(null, problem);
}
