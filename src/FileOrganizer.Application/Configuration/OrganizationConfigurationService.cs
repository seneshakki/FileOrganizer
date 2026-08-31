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
            ConfigurationTextReadStatus.Found => DecodeLoad(read.Content!),
            ConfigurationTextReadStatus.NotFound => ConfigurationLoadResult.NotFound(),
            ConfigurationTextReadStatus.Failed =>
                ConfigurationLoadResult.StorageFailure(read.Problems),
            _ => throw new InvalidOperationException("The configuration store returned an unknown status."),
        };
    }

    public async ValueTask<ConfigurationSaveResult> SaveAsync(
        string path,
        OrganizationConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(configuration);
        if (!encoded.Succeeded)
        {
            return ConfigurationSaveResult.Invalid(encoded.Problems);
        }

        ConfigurationTextWriteResult write = await _store.WriteAsync(
            path,
            encoded.Json!,
            cancellationToken);
        return write.Status == ConfigurationTextWriteStatus.Succeeded
            ? ConfigurationSaveResult.Saved()
            : ConfigurationSaveResult.StorageFailure(write.Problems);
    }

    public ValueTask<ConfigurationSaveResult> ExportAsync(
        string destinationPath,
        OrganizationConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        SaveAsync(destinationPath, configuration, cancellationToken);

    public async ValueTask<ConfigurationImportResult> ImportAsync(
        string sourcePath,
        string activeConfigurationPath,
        CancellationToken cancellationToken = default)
    {
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
            activeConfigurationPath,
            encoded.Json!,
            cancellationToken);
        return write.Status == ConfigurationTextWriteStatus.Succeeded
            ? ConfigurationImportResult.Imported(decoded.Configuration!)
            : ConfigurationImportResult.StorageFailure(write.Problems);
    }

    private static ConfigurationLoadResult DecodeLoad(string content)
    {
        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(content);
        return decoded.Succeeded
            ? ConfigurationLoadResult.Loaded(decoded.Configuration!, content)
            : ConfigurationLoadResult.Invalid(decoded.Problems, content);
    }
}
