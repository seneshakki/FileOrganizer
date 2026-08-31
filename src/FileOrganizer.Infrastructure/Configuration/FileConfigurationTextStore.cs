using System.Diagnostics;
using System.Text;
using FileOrganizer.Application.Configuration;

namespace FileOrganizer.Infrastructure.Configuration;

/// <summary>
/// Reads and atomically publishes UTF-8 configuration documents at caller-selected paths.
/// </summary>
/// <remarks>
/// A save writes and flushes a complete same-directory temporary file before publishing it.
/// The final rename is atomic where the selected filesystem supports atomic replacement.
/// Create-if-absent publication uses a no-overwrite rename. On Windows, every participating
/// FileOrganizer writer in the current Windows login session holds one named publication mutex
/// across validation and replacement, including when the same entry is addressed through a
/// different path alias. Writers outside that session or protocol can still race the final
/// validation/replace window because documented managed APIs do not provide an atomic
/// compare-and-replace primitive for an existing file.
/// .NET does not expose a portable directory flush, so this type does not claim that a new
/// directory entry survives every sudden-power-loss scenario.
/// </remarks>
public sealed class FileConfigurationTextStore : IConfigurationTextStore
{
    private static readonly ConfigurationPathGateRegistry SharedPathGates = new();

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly IConfigurationFilePublisher _publisher;
    private readonly ConfigurationPathGateRegistry _pathGates;
    private readonly IConfigurationPathIdentityComparer _pathIdentityComparer;
    private readonly IConfigurationPublicationGate _publicationGate;

    public FileConfigurationTextStore()
        : this(
            new DurableConfigurationFilePublisher(),
            new WindowsConfigurationPathIdentityComparer(),
            new WindowsConfigurationPublicationGate(),
            SharedPathGates)
    {
    }

    internal FileConfigurationTextStore(IConfigurationFilePublisher publisher)
        : this(
            publisher,
            new WindowsConfigurationPathIdentityComparer(),
            new WindowsConfigurationPublicationGate(),
            SharedPathGates)
    {
    }

    internal FileConfigurationTextStore(
        IConfigurationFilePublisher publisher,
        IConfigurationPathIdentityComparer pathIdentityComparer)
        : this(
            publisher,
            pathIdentityComparer,
            new WindowsConfigurationPublicationGate(),
            SharedPathGates)
    {
    }

    internal FileConfigurationTextStore(
        IConfigurationFilePublisher publisher,
        IConfigurationPathIdentityComparer pathIdentityComparer,
        IConfigurationPublicationGate publicationGate,
        ConfigurationPathGateRegistry? pathGates = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(pathIdentityComparer);
        ArgumentNullException.ThrowIfNull(publicationGate);
        _publisher = publisher;
        _pathGates = pathGates ?? SharedPathGates;
        _pathIdentityComparer = pathIdentityComparer;
        _publicationGate = publicationGate;
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

        string canonicalPathKey = ConfigurationPublicationPath.CreateCanonicalKey(fullPath);
        IAsyncDisposable pathLease;
        try
        {
            pathLease = await _pathGates.AcquireAsync(
                canonicalPathKey,
                _publicationGate.AcquisitionTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ReadFailure(
                ConfigurationStorageProblemKind.PublicationSynchronizationCanceled,
                "Configuration access was canceled while waiting for exclusive access.");
        }
        catch (TimeoutException)
        {
            return ReadFailure(
                ConfigurationStorageProblemKind.PublicationSynchronizationUnavailable,
                "Exclusive configuration access could not be established in time.");
        }

        await using (pathLease)
        {
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
                string content = StrictUtf8.GetString(
                    bytes,
                    contentOffset,
                    bytes.Length - contentOffset);
                return ConfigurationTextReadResult.Found(
                    content,
                    ConfigurationDocumentRevision.FromBytes(bytes));
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
        }
    }

    public async ValueTask<ConfigurationTextWriteResult> WriteAsync(
        ConfigurationTextWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetFullFilePath(request.Path, out string fullPath))
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.InvalidPath,
                "The configuration path must be a fully qualified file path.");
        }

        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(request.Content);
        }
        catch (EncoderFallbackException)
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.WriteFailure,
                "The configuration document cannot be encoded as valid UTF-8 text.");
        }

        string canonicalPathKey = ConfigurationPublicationPath.CreateCanonicalKey(fullPath);
        long acquisitionStartedTimestamp = Stopwatch.GetTimestamp();
        IAsyncDisposable pathLease;
        try
        {
            pathLease = await _pathGates.AcquireAsync(
                canonicalPathKey,
                _publicationGate.AcquisitionTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.PublicationSynchronizationCanceled,
                "Configuration publication was canceled while waiting for exclusive access.");
        }
        catch (TimeoutException)
        {
            return WriteFailure(
                ConfigurationStorageProblemKind.PublicationSynchronizationUnavailable,
                "Exclusive configuration publication access could not be established in time.");
        }

        await using (pathLease)
        {
            IConfigurationPublicationLease publicationLease;
            try
            {
                TimeSpan remainingAcquisitionTime = RemainingAcquisitionTime(
                    acquisitionStartedTimestamp,
                    _publicationGate.AcquisitionTimeout);
                publicationLease = await _publicationGate.AcquireAsync(
                    canonicalPathKey,
                    remainingAcquisitionTime,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return WriteFailure(
                    ConfigurationStorageProblemKind.PublicationSynchronizationCanceled,
                    "Configuration publication was canceled while waiting for exclusive access.");
            }
            catch (ConfigurationPublicationGateException)
            {
                return WriteFailure(
                    ConfigurationStorageProblemKind.PublicationSynchronizationUnavailable,
                    "Exclusive configuration publication access could not be established.");
            }

            await using (publicationLease)
            {
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

                    ConfigurationTextWriteResult? conflict = await ValidateWriteConditionsAsync(
                        fullPath,
                        request,
                        cancellationToken).ConfigureAwait(false);
                    if (conflict is not null)
                    {
                        return conflict;
                    }

                    string directory = Path.GetDirectoryName(fullPath)!;
                    Directory.CreateDirectory(directory);
                    unsafePath = InspectExistingPath(fullPath);
                    if (unsafePath is not null)
                    {
                        return ConfigurationTextWriteResult.Failed([unsafePath]);
                    }

                    conflict = await ValidateWriteConditionsAsync(
                        fullPath,
                        request,
                        cancellationToken).ConfigureAwait(false);
                    if (conflict is not null)
                    {
                        return conflict;
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
                    unsafePath = InspectExistingPath(fullPath);
                    if (unsafePath is not null)
                    {
                        return ConfigurationTextWriteResult.Failed([unsafePath]);
                    }

                    conflict = await ValidateWriteConditionsAsync(
                        fullPath,
                        request,
                        cancellationToken).ConfigureAwait(false);
                    if (conflict is not null)
                    {
                        return conflict;
                    }

                    publicationStarted = true;
                    if (request.Precondition.Kind
                        == ConfigurationWritePreconditionKind.MustBeMissing)
                    {
                        _publisher.PublishNew(temporaryPath, fullPath);
                    }
                    else if (request.Precondition.Kind
                             == ConfigurationWritePreconditionKind.MustMatchRevision)
                    {
                        _publisher.ReplaceExisting(temporaryPath, fullPath);
                    }
                    else if (File.Exists(fullPath))
                    {
                        _publisher.ReplaceExisting(temporaryPath, fullPath);
                    }
                    else
                    {
                        _publisher.PublishNew(temporaryPath, fullPath);
                    }

                    temporaryPath = null;
                    return ConfigurationTextWriteResult.Succeeded(
                        ConfigurationDocumentRevision.FromBytes(bytes));
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
                    if (publicationStarted)
                    {
                        ConfigurationTextWriteResult? conflict =
                            await TryClassifyPublicationConflictAsync(
                                fullPath,
                                request,
                                cancellationToken).ConfigureAwait(false);
                        if (conflict is not null)
                        {
                            return conflict;
                        }
                    }

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
                }
            }
        }
    }

    private static TimeSpan RemainingAcquisitionTime(
        long startedTimestamp,
        TimeSpan totalTimeout)
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(startedTimestamp);
        return elapsed >= totalTimeout ? TimeSpan.Zero : totalTimeout - elapsed;
    }

    private async ValueTask<ConfigurationTextWriteResult?> ValidateWriteConditionsAsync(
        string fullPath,
        ConfigurationTextWriteRequest request,
        CancellationToken cancellationToken)
    {
        ConfigurationTextWriteResult? preconditionConflict =
            await ValidatePreconditionAsync(
                fullPath,
                request.Precondition,
                cancellationToken).ConfigureAwait(false);
        if (preconditionConflict is not null)
        {
            return preconditionConflict;
        }

        foreach (string protectedPath in request.ProtectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConfigurationPathIdentityComparison comparison =
                _pathIdentityComparer.CompareExisting(
                    fullPath,
                    protectedPath,
                    cancellationToken);
            if (comparison == ConfigurationPathIdentityComparison.SameEntry)
            {
                return Conflict(
                    ConfigurationWriteConflictKind.ProtectedPathAlias,
                    "The export destination identifies a protected configuration file.");
            }

            if (comparison == ConfigurationPathIdentityComparison.Unverifiable)
            {
                return Conflict(
                    ConfigurationWriteConflictKind.ProtectedPathIdentityUnavailable,
                    "The export destination could not be proven distinct from a protected configuration file.");
            }
        }

        return null;
    }

    private static async ValueTask<ConfigurationTextWriteResult?> ValidatePreconditionAsync(
        string fullPath,
        ConfigurationWritePrecondition precondition,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (precondition.Kind)
        {
            case ConfigurationWritePreconditionKind.Unconditional:
                return null;
            case ConfigurationWritePreconditionKind.MustBeMissing:
                return File.Exists(fullPath)
                    ? Conflict(
                        ConfigurationWriteConflictKind.TargetAlreadyExists,
                        "A configuration file appeared after the destination was selected.")
                    : null;
            case ConfigurationWritePreconditionKind.MustMatchRevision:
                if (!File.Exists(fullPath))
                {
                    return Conflict(
                        ConfigurationWriteConflictKind.ChangedExternally,
                        "The configuration file changed externally after it was loaded.");
                }

                byte[] currentBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken)
                    .ConfigureAwait(false);
                ConfigurationDocumentRevision currentRevision =
                    ConfigurationDocumentRevision.FromBytes(currentBytes);
                return currentRevision == precondition.ExpectedRevision
                    ? null
                    : Conflict(
                        ConfigurationWriteConflictKind.ChangedExternally,
                        "The configuration file changed externally after it was loaded.");
            default:
                throw new InvalidOperationException("Unknown configuration write precondition.");
        }
    }

    private async ValueTask<ConfigurationTextWriteResult?> TryClassifyPublicationConflictAsync(
        string fullPath,
        ConfigurationTextWriteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ValidateWriteConditionsAsync(fullPath, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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

    private static ConfigurationTextWriteResult Conflict(
        ConfigurationWriteConflictKind kind,
        string description) =>
        ConfigurationTextWriteResult.Conflicted(
            new ConfigurationWriteConflict(kind, description));

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
