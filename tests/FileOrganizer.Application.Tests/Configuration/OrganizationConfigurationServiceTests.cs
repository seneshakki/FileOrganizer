using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Actions;
using FileOrganizer.Core.Matching.Conditions;
using FileOrganizer.Core.Paths;
using FileOrganizer.Core.Planning;
using FileOrganizer.Core.Rules;

namespace FileOrganizer.Application.Tests.Configuration;

public sealed class OrganizationConfigurationServiceTests
{
    [Fact]
    public async Task SaveLoadAndExportUseTheTextStorageBoundary()
    {
        ScriptedTextStore store = new();
        OrganizationConfigurationService service = new(store);
        OrganizationConfiguration configuration = Configuration();

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ConfigurationSaveResult save = await service.SaveAsync(
            "active.json",
            configuration,
            cancellationToken);
        ConfigurationLoadResult load = await service.LoadAsync("active.json", cancellationToken);
        ConfigurationSaveResult export = await service.ExportAsync(
            "export.json",
            configuration,
            cancellationToken);

        Assert.Equal(ConfigurationSaveStatus.Saved, save.Status);
        Assert.Equal(ConfigurationLoadStatus.Loaded, load.Status);
        Assert.Equal(ConfigurationSaveStatus.Saved, export.Status);
        Assert.NotNull(load.Configuration);
        Assert.Equal(2, store.WriteCount);
        Assert.True(store.Files.ContainsKey("export.json"));
    }

    [Fact]
    public async Task InvalidImportNeverReplacesTheActiveConfiguration()
    {
        ScriptedTextStore store = new();
        store.Files["import.json"] = "{ broken";
        store.Files["active.json"] = "previous valid content";
        OrganizationConfigurationService service = new(store);

        ConfigurationImportResult result = await service.ImportAsync(
            "import.json",
            "active.json",
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationImportStatus.Invalid, result.Status);
        Assert.Equal("previous valid content", store.Files["active.json"]);
        Assert.Equal(0, store.WriteCount);
        Assert.Contains(
            result.ConfigurationProblems,
            static problem => problem.Kind == ConfigurationProblemKind.MalformedJson);
    }

    [Fact]
    public async Task ValidImportPublishesAValidatedReplacementWithoutMerging()
    {
        ScriptedTextStore store = new();
        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(Configuration());
        store.Files["import.json"] = encoded.Json!;
        store.Files["active.json"] = "old";
        OrganizationConfigurationService service = new(store);

        ConfigurationImportResult result = await service.ImportAsync(
            "import.json",
            "active.json",
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationImportStatus.Imported, result.Status);
        Assert.NotNull(result.Configuration);
        Assert.Equal(1, store.WriteCount);
        Assert.NotEqual("old", store.Files["active.json"]);
        Assert.Equal(encoded.Json, store.Files["active.json"]);
    }

    [Fact]
    public async Task StorageFailuresRemainStructured()
    {
        ScriptedTextStore store = new()
        {
            WriteResult = ConfigurationTextWriteResult.Failed(
                [
                    new ConfigurationStorageProblem(
                        ConfigurationStorageProblemKind.PublicationFailure,
                        "Publication failed."),
                ]),
        };
        OrganizationConfigurationService service = new(store);

        ConfigurationSaveResult result = await service.SaveAsync(
            "active.json",
            Configuration(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationSaveStatus.StorageFailure, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationFailure,
            Assert.Single(result.StorageProblems).Kind);
    }

    [Fact]
    public async Task CancellationPropagatesThroughTheStorageBoundary()
    {
        ScriptedTextStore store = new();
        OrganizationConfigurationService service = new(store);
        using CancellationTokenSource source = new();
        source.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.LoadAsync("active.json", source.Token));
    }

    private static OrganizationConfiguration Configuration() =>
        new(
            new PathSemantics(
                PathSyntax.Windows,
                PathCaseSensitivity.Insensitive,
                PathUnicodeNormalization.FormC),
            [
                new ConfiguredOrganizationRule(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    new OrganizationRule(
                        new Rule("Rule", new GlobCondition("*.txt")),
                        new MoveToDirectoryAction(@"C:\Target"))),
            ]);

    private sealed class ScriptedTextStore : IConfigurationTextStore
    {
        internal Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        internal int WriteCount { get; private set; }

        internal ConfigurationTextWriteResult WriteResult { get; init; } =
            ConfigurationTextWriteResult.Succeeded();

        public ValueTask<ConfigurationTextReadResult> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                Files.TryGetValue(path, out string? content)
                    ? ConfigurationTextReadResult.Found(content)
                    : ConfigurationTextReadResult.NotFound());
        }

        public ValueTask<ConfigurationTextWriteResult> WriteAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            if (WriteResult.Status == ConfigurationTextWriteStatus.Succeeded)
            {
                Files[path] = content;
            }

            return ValueTask.FromResult(WriteResult);
        }
    }
}
