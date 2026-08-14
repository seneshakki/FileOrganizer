using System.Collections.Concurrent;
using System.Text;
using FileOrganizer.Application.Configuration;

namespace FileOrganizer.Infrastructure.Configuration;

/// <summary>
/// Reads and atomically publishes UTF-8 configuration documents at caller-selected paths.
/// </summary>
/// <remarks>
/// A save writes and flushes a complete same-directory temporary file before publishing it.
/// The final rename is atomic where the selected filesystem supports atomic replacement.
/// .NET does not expose a portable directory flush, so this type does not claim that a new
/// directory entry survives every sudden-power-loss scenario.
/// </remarks>
public sealed class FileConfigurationTextStore : IConfigurationTextStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PathGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IConfigurationFilePublisher _publisher;

    public FileConfigurationTextStore()
        : this(new DurableConfigurationFilePublisher())
    {
    }

    internal FileConfigurationTextStore(IConfigurationFilePublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        _publisher = publisher;
    }

    public async ValueTask<ConfigurationTextReadResult> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetFullFilePath(path, out string fullPath))
        {
            return ReadFailure(
                ConfigurationStorageProblemKind.InvalidPath,
                "The configuration path must be a fully qualified file path.");
        }

        SemaphoreSlim gate = PathGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConfigurationStorageProblem? unsafePath = InspectExistingPath(fullPath);
            if (unsafePath is not null)
            {
                return ConfigurationTextReadResult.Failed([unsafePath]);
            }

            if (!File.Exists(fullPath))
            {
                return ConfigurationTextReadResult.NotFound();
            }

            byte[] bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken)
                .ConfigureAwait(false);
            int contentOffset = HasUtf8Preamble(bytes) ? Utf8Preamble.Length : 0;
            string content = StrictUtf8.GetString(bytes, contentOffset, bytes.Length - contentOffset);
            return ConfigurationTextReadResult.Found(content);
        }
        catch (UnauthorizedAccessException)
        {
            return ReadFailure(
                ConfigurationStorageProblemKind.Inaccessible,
                "The configuration file could not be accessed.");
        }
        catch (DecoderFallbackException)
        {
            return ReadFailure(
                ConfigurationStorageProblemKind.ReadFailure,
                "The configuration file is not valid UTF-8 text.");
        }
        catch (IOException)
        {
            return ReadFailure(
                ConfigurationStorageProblemKind.ReadFailure,
                "The configuration file could not be read.");
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask<ConfigurationTextWriteResult> WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetFullFilePath(path, out string fullPath))
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.InvalidPath,
                "The configuration path must be a fully qualified file path.");
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.WriteFailure,
                "The configuration document cannot be encoded as valid UTF-8 text.");
        }

        SemaphoreSlim gate = PathGates.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        bool publicationStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigurationStorageProblem? unsafePath = InspectExistingPath(fullPath);
            if (unsafePath is not null)
            {
                return ConfigurationTextWriteResult.Failed([unsafePath]);
            }

            string directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            unsafePath = InspectExistingPath(fullPath);
            if (unsafePath is not null)
            {
                return ConfigurationTextWriteResult.Failed([unsafePath]);
            }

            temporaryPath = Path.Combine(
                directory,
                $".fileorganizer-config-{Guid.NewGuid():N}.tmp");
            cancellationToken.ThrowIfCancellationRequested();
            using (FileStream stream = new(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            publicationStarted = true;
            if (File.Exists(fullPath))
            {
                _publisher.ReplaceExisting(temporaryPath, fullPath);
            }
            else
            {
                _publisher.PublishNew(temporaryPath, fullPath);
            }

            temporaryPath = null;
            return ConfigurationTextWriteResult.Succeeded();
        }
        catch (UnauthorizedAccessException)
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.Inaccessible,
                "The configuration file could not be written.");
        }
        catch (PlatformNotSupportedException) when (publicationStarted)
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.AtomicPublicationUnavailable,
                "Atomic configuration replacement is unavailable on this platform.");
        }
        catch (NotSupportedException) when (publicationStarted)
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.AtomicPublicationUnavailable,
                "Atomic configuration replacement is unavailable on this filesystem.");
        }
        catch (IOException)
        {
            return WriteFailure(
                publicationStarted
                    ? ConfigurationStorageProblemKind.PublicationFailure
                    : ConfigurationStorageProblemKind.WriteFailure,
                publicationStarted
                    ? "The completed configuration document could not be published."
                    : "The configuration document could not be written.");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDeleteTemporaryFile(temporaryPath);
            }

            gate.Release();
        }
    }

    private static ConfigurationStorageProblem? InspectExistingPath(string fullPath)
    {
        if (Directory.Exists(fullPath))
        {
            return new ConfigurationStorageProblem(
                ConfigurationStorageProblemKind.InvalidPath,
                "The configuration file path is occupied by a directory.");
        }

        if (File.Exists(fullPath) && IsReparsePoint(fullPath))
        {
            return new ConfigurationStorageProblem(
                ConfigurationStorageProblemKind.InvalidPath,
                "The configuration file cannot be a reparse point.");
        }

        string? current = Path.GetDirectoryName(fullPath);
        while (current is not null)
        {
            if (File.Exists(current))
            {
                return new ConfigurationStorageProblem(
                    ConfigurationStorageProblemKind.InvalidPath,
                    "A configuration path ancestor is not a directory.");
            }

            if (Directory.Exists(current) && IsReparsePoint(current))
            {
                return new ConfigurationStorageProblem(
                    ConfigurationStorageProblemKind.InvalidPath,
                    "A configuration path ancestor cannot be a reparse point.");
            }

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }

    private static bool TryGetFullFilePath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            string candidate = Path.GetFullPath(path);
            string root = Path.GetPathRoot(candidate)!;
            if (StringComparerForHost.Equals(
                    Path.TrimEndingDirectorySeparator(candidate),
                    Path.TrimEndingDirectorySeparator(root))
                || string.IsNullOrEmpty(Path.GetFileName(candidate)))
            {
                return false;
            }

            fullPath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }
    }

    private static StringComparer StringComparerForHost => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasUtf8Preamble(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(Utf8Preamble);

    private static ReadOnlySpan<byte> Utf8Preamble => [0xef, 0xbb, 0xbf];

    private static ConfigurationTextReadResult ReadFailure(
        ConfigurationStorageProblemKind kind,
        string description) =>
        ConfigurationTextReadResult.Failed([new ConfigurationStorageProblem(kind, description)]);

    private static ConfigurationTextWriteResult WriteFailure(
        ConfigurationStorageProblemKind kind,
        string description) =>
        ConfigurationTextWriteResult.Failed([new ConfigurationStorageProblem(kind, description)]);

    private static void TryDeleteTemporaryFile(string path)
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
