namespace FileOrganizer.Application.Configuration;

/// <summary>Coordinates validated configuration load, save, import, and export.</summary>
public sealed class OrganizationConfigurationService
{
    private readonly IConfigurationTextStore _store;

    public OrganizationConfigurationService(IConfigurationTextStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async ValueTask<ConfigurationLoadResult> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ConfigurationTextReadResult read = await _store.ReadAsync(path, cancellationToken);
        return read.Status switch
        {
            ConfigurationTextReadStatus.Found => DecodeLoad(read.Content!, read.Revision!),
            ConfigurationTextReadStatus.NotFound => ConfigurationLoadResult.NotFound(),
            ConfigurationTextReadStatus.Failed =>
                ConfigurationLoadResult.StorageFailure(read.Problems),
            _ => throw new InvalidOperationException("The configuration store returned an unknown status."),
        };
    }

    public async ValueTask<ConfigurationSaveResult> SaveAsync(
        string path,
        OrganizationConfiguration configuration,
        ConfigurationWritePrecondition precondition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(precondition);

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(configuration);
        if (!encoded.Succeeded)
        {
            return ConfigurationSaveResult.Invalid(encoded.Problems);
        }

        ConfigurationTextWriteResult write = await _store.WriteAsync(
            new ConfigurationTextWriteRequest(path, encoded.Json!, precondition),
            cancellationToken);
        return write.Status switch
        {
            ConfigurationTextWriteStatus.Succeeded =>
                ConfigurationSaveResult.Saved(write.Revision!),
            ConfigurationTextWriteStatus.Conflict =>
                ConfigurationSaveResult.Conflicted(write.Conflict!),
            ConfigurationTextWriteStatus.Failed =>
                ConfigurationSaveResult.StorageFailure(write.Problems),
            _ => throw new InvalidOperationException(
                "The configuration store returned an unknown write status."),
        };
    }

    public async ValueTask<ConfigurationSaveResult> ExportAsync(
        string destinationPath,
        OrganizationConfiguration configuration,
        IEnumerable<string> protectedConfigurationPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(protectedConfigurationPaths);

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(configuration);
        if (!encoded.Succeeded)
        {
            return ConfigurationSaveResult.Invalid(encoded.Problems);
        }

        ConfigurationTextWriteResult write = await _store.WriteAsync(
            new ConfigurationTextWriteRequest(
                destinationPath,
                encoded.Json!,
                ConfigurationWritePrecondition.Unconditional,
                protectedConfigurationPaths),
            cancellationToken);
        return write.Status switch
        {
            ConfigurationTextWriteStatus.Succeeded =>
                ConfigurationSaveResult.Saved(write.Revision!),
            ConfigurationTextWriteStatus.Conflict =>
                ConfigurationSaveResult.Conflicted(write.Conflict!),
            ConfigurationTextWriteStatus.Failed =>
                ConfigurationSaveResult.StorageFailure(write.Problems),
            _ => throw new InvalidOperationException(
                "The configuration store returned an unknown write status."),
        };
    }

    public async ValueTask<ConfigurationImportResult> ImportAsync(
        string sourcePath,
        string activeConfigurationPath,
        ConfigurationWritePrecondition precondition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(precondition);

        ConfigurationTextReadResult read = await _store.ReadAsync(sourcePath, cancellationToken);
        if (read.Status == ConfigurationTextReadStatus.NotFound)
        {
            return ConfigurationImportResult.SourceNotFound();
        }

        if (read.Status == ConfigurationTextReadStatus.Failed)
        {
            return ConfigurationImportResult.StorageFailure(read.Problems);
        }

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(read.Content!);
        if (!decoded.Succeeded)
        {
            return ConfigurationImportResult.Invalid(decoded.Problems);
        }

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(
            decoded.Configuration!);
        if (!encoded.Succeeded)
        {
            return ConfigurationImportResult.Invalid(encoded.Problems);
        }

        ConfigurationTextWriteResult write = await _store.WriteAsync(
            new ConfigurationTextWriteRequest(
                activeConfigurationPath,
                encoded.Json!,
                precondition),
            cancellationToken);
        return write.Status switch
        {
            ConfigurationTextWriteStatus.Succeeded =>
                ConfigurationImportResult.Imported(decoded.Configuration!, write.Revision!),
            ConfigurationTextWriteStatus.Conflict =>
                ConfigurationImportResult.Conflicted(write.Conflict!),
            ConfigurationTextWriteStatus.Failed =>
                ConfigurationImportResult.StorageFailure(write.Problems),
            _ => throw new InvalidOperationException(
                "The configuration store returned an unknown write status."),
        };
    }

    private static ConfigurationLoadResult DecodeLoad(
        string content,
        ConfigurationDocumentRevision revision)
    {
        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(content);
        return decoded.Succeeded
            ? ConfigurationLoadResult.Loaded(decoded.Configuration!, content, revision)
            : ConfigurationLoadResult.Invalid(decoded.Problems, content, revision);
    }
}
