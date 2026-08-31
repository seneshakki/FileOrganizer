using System.Text;
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
            ConfigurationWritePrecondition.MustBeMissing,
            cancellationToken);
        ConfigurationLoadResult load = await service.LoadAsync("active.json", cancellationToken);
        ConfigurationSaveResult export = await service.ExportAsync(
            "export.json",
            configuration,
            [],
            cancellationToken);

        Assert.Equal(ConfigurationSaveStatus.Saved, save.Status);
        Assert.Equal(ConfigurationLoadStatus.Loaded, load.Status);
        Assert.Equal(ConfigurationSaveStatus.Saved, export.Status);
        Assert.NotNull(load.Configuration);
        Assert.Equal(store.Files["active.json"], load.SourceText);
        Assert.NotNull(load.Revision);
        Assert.Equal(2, store.WriteCount);
        Assert.True(store.Files.ContainsKey("export.json"));
    }

    [Fact]
    public async Task ValidLoadPreservesTheExactSourceText()
    {
        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(Configuration());
        string sourceText = " \r\n" + encoded.Json + "\r\n";
        ScriptedTextStore store = new();
        store.Files["active.json"] = sourceText;
        OrganizationConfigurationService service = new(store);

        ConfigurationLoadResult load = await service.LoadAsync(
            "active.json",
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadStatus.Loaded, load.Status);
        Assert.NotNull(load.Configuration);
        Assert.Equal(sourceText, load.SourceText);
        Assert.Equal(Revision(sourceText), load.Revision);
        Assert.Empty(load.ConfigurationProblems);
    }

    [Fact]
    public async Task InvalidLoadPreservesTheExactMalformedSourceText()
    {
        const string malformed = " \r\n{ broken\r\n";
        ScriptedTextStore store = new();
        store.Files["active.json"] = malformed;
        OrganizationConfigurationService service = new(store);

        ConfigurationLoadResult load = await service.LoadAsync(
            "active.json",
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadStatus.Invalid, load.Status);
        Assert.Null(load.Configuration);
        Assert.Equal(malformed, load.SourceText);
        Assert.Equal(Revision(malformed), load.Revision);
        Assert.Contains(
            load.ConfigurationProblems,
            static problem => problem.Kind == ConfigurationProblemKind.MalformedJson);
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
            ConfigurationWritePrecondition.MustMatch(Revision("previous valid content")),
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
            ConfigurationWritePrecondition.MustMatch(Revision("old")),
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
            ConfigurationWritePrecondition.MustBeMissing,
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationSaveStatus.StorageFailure, result.Status);
        Assert.Equal(
            ConfigurationStorageProblemKind.PublicationFailure,
            Assert.Single(result.StorageProblems).Kind);
    }

    [Fact]
    public async Task ConditionalWriteConflictsRemainStructured()
    {
        ScriptedTextStore store = new()
        {
            WriteResult = ConfigurationTextWriteResult.Conflicted(
                new ConfigurationWriteConflict(
                    ConfigurationWriteConflictKind.ChangedExternally,
                    "The document changed externally.")),
        };
        OrganizationConfigurationService service = new(store);

        ConfigurationSaveResult result = await service.SaveAsync(
            "active.json",
            Configuration(),
            ConfigurationWritePrecondition.MustMatch(Revision("loaded")),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationSaveStatus.Conflict, result.Status);
        Assert.Equal(ConfigurationWriteConflictKind.ChangedExternally, result.Conflict!.Kind);
        Assert.Empty(result.ConfigurationProblems);
        Assert.Empty(result.StorageProblems);
    }

    [Fact]
    public void WriteRequestSnapshotsProtectedPaths()
    {
        List<string> protectedPaths = ["active.json"];
        ConfigurationTextWriteRequest request = new(
            "export.json",
            "{}",
            ConfigurationWritePrecondition.Unconditional,
            protectedPaths);

        protectedPaths[0] = "mutated.json";
        protectedPaths.Add("added.json");

        Assert.Equal(["active.json"], request.ProtectedPaths);
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

    private static ConfigurationDocumentRevision Revision(string content) =>
        ConfigurationDocumentRevision.FromBytes(Encoding.UTF8.GetBytes(content));

    private sealed class ScriptedTextStore : IConfigurationTextStore
    {
        internal Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        internal int WriteCount { get; private set; }

        internal ConfigurationTextWriteResult? WriteResult { get; init; }

        public ValueTask<ConfigurationTextReadResult> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                Files.TryGetValue(path, out string? content)
                    ? ConfigurationTextReadResult.Found(content, Revision(content))
                    : ConfigurationTextReadResult.NotFound());
        }

        public ValueTask<ConfigurationTextWriteResult> WriteAsync(
            ConfigurationTextWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            ConfigurationTextWriteResult result = WriteResult ??
                EvaluateWrite(request);
            if (result.Status == ConfigurationTextWriteStatus.Succeeded)
            {
                Files[request.Path] = request.Content;
            }

            return ValueTask.FromResult(result);
        }

        private ConfigurationTextWriteResult EvaluateWrite(
            ConfigurationTextWriteRequest request)
        {
            bool exists = Files.TryGetValue(request.Path, out string? current);
            if (request.Precondition.Kind == ConfigurationWritePreconditionKind.MustBeMissing
                && exists)
            {
                return Conflict(ConfigurationWriteConflictKind.TargetAlreadyExists);
            }

            if (request.Precondition.Kind
                    == ConfigurationWritePreconditionKind.MustMatchRevision
                && (!exists || Revision(current!) != request.Precondition.ExpectedRevision))
            {
                return Conflict(ConfigurationWriteConflictKind.ChangedExternally);
            }

            return ConfigurationTextWriteResult.Succeeded(Revision(request.Content));
        }

        private static ConfigurationTextWriteResult Conflict(
            ConfigurationWriteConflictKind kind) =>
            ConfigurationTextWriteResult.Conflicted(
                new ConfigurationWriteConflict(kind, "Injected conflict."));
    }
}
