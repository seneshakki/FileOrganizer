using System.Text;
using FileOrganizer.App.Services;
using FileOrganizer.App.Startup;
using FileOrganizer.App.ViewModels;
using FileOrganizer.Application.Configuration;

namespace FileOrganizer.App.Tests.ViewModels;

public sealed class ConfigurationEditorViewModelTests
{
    private static readonly string ActivePath = Path.Combine(
        Path.GetTempPath(),
        "file-organizer-app-tests",
        "active.json");
    private static readonly string ImportPath = Path.Combine(
        Path.GetTempPath(),
        "file-organizer-app-tests",
        "import.json");
    private static readonly string ExportPath = Path.Combine(
        Path.GetTempPath(),
        "file-organizer-app-tests",
        "export.json");

    [Fact]
    public async Task FirstRunWithoutAPathPerformsNoIoOrActivation()
    {
        ScriptedTextStore store = new();
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            ConfigurationPathArguments.Parse([]),
            store,
            new QueuePathPicker(),
            activation);

        await editor.InitializeAsync();

        Assert.Equal(ConfigurationLoadState.AwaitingPath, editor.LoadState);
        Assert.False(editor.HasDocument);
        Assert.Equal(0, store.ReadCount);
        Assert.Equal(0, store.WriteCount);
        Assert.Empty(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task InvalidStartupDocumentRemainsEditableWithoutActivationOrWrite()
    {
        const string malformed = " \r\n{ broken\r\n";
        ScriptedTextStore store = new();
        store.Files[ActivePath] = malformed;
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);

        await editor.InitializeAsync();

        Assert.Equal(ConfigurationLoadState.Invalid, editor.LoadState);
        Assert.Equal(malformed, editor.ConfigurationText);
        Assert.True(editor.HasDocument);
        Assert.False(editor.IsDirty);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("MalformedJson", StringComparison.Ordinal));
        Assert.Equal(0, store.WriteCount);
        Assert.Empty(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task FailedOpenKeepsCurrentDocumentAndNamesBothPaths()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        QueuePathPicker picker = new(openPaths: [ImportPath]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            picker,
            activation);
        await editor.InitializeAsync();
        string previousText = editor.ConfigurationText;
        store.ReadAsyncOverride = (path, _) => ValueTask.FromResult(
            path == ImportPath
                ? ConfigurationTextReadResult.Failed(
                    [
                        new ConfigurationStorageProblem(
                            ConfigurationStorageProblemKind.Inaccessible,
                            "Injected read failure."),
                    ])
                : ConfigurationTextReadResult.NotFound());

        await editor.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.StorageFailure, editor.LoadState);
        Assert.Equal(ActivePath, editor.ConfigurationPath);
        Assert.Equal(ActivePath, editor.ActiveConfigurationPath);
        Assert.Equal(previousText, editor.ConfigurationText);
        Assert.Contains(ImportPath, editor.Status, StringComparison.Ordinal);
        Assert.Contains(ActivePath, editor.Status, StringComparison.Ordinal);
        Assert.Single(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task ExplicitCreatePublishesBeforeActivatingAnEmptySchemaV1Configuration()
    {
        List<string> sequence = [];
        ScriptedTextStore store = new()
        {
            WriteObserver = (_, _) => sequence.Add("write"),
        };
        QueuePathPicker picker = new(savePaths: [ActivePath]);
        RecordingActivation activation = new(_ => sequence.Add("activate"));
        ConfigurationEditorViewModel editor = CreateEditor(
            ConfigurationPathArguments.Parse([]),
            store,
            picker,
            activation);
        await editor.InitializeAsync();

        await editor.CreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["write", "activate"], sequence);
        Assert.Equal(1, store.WriteCount);
        string json = store.Files[ActivePath];
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"watchRoots\": []", json, StringComparison.Ordinal);
        Assert.Contains("\"rules\": []", json, StringComparison.Ordinal);
        Assert.Equal(ConfigurationLoadState.Loaded, editor.LoadState);
        Assert.Equal(ActivePath, editor.ConfigurationPath);
        Assert.Equal(ActivePath, editor.ActiveConfigurationPath);
        Assert.False(editor.IsDirty);
        Assert.Single(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task MissingStartupPathIsNeverReplacedIfAnotherProcessCreatesIt()
    {
        ScriptedTextStore store = new();
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        store.Files[ActivePath] = "external";

        await editor.CreateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        Assert.Equal("external", store.Files[ActivePath]);
        Assert.Empty(activation.Configurations);
        Assert.Equal(
            ConfigurationWritePreconditionKind.MustBeMissing,
            store.LastWriteRequest!.Precondition.Kind);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("TargetAlreadyExists", StringComparison.Ordinal));
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task ValidDraftAtMissingStartupPathSavesOnlyWhilePathRemainsAbsent()
    {
        ScriptedTextStore store = new();
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        editor.ConfigurationText = EmptyConfigurationJson();

        await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Loaded, editor.LoadState);
        Assert.Equal(
            ConfigurationWritePreconditionKind.MustBeMissing,
            store.LastWriteRequest!.Precondition.Kind);
        Assert.True(store.Files.ContainsKey(ActivePath));
        Assert.False(editor.IsDirty);
        Assert.Single(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task ValidDraftAtMissingStartupPathDoesNotReplaceAnAppearingFile()
    {
        ScriptedTextStore store = new();
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        editor.ConfigurationText = EmptyConfigurationJson();
        store.Files[ActivePath] = "external";

        await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        Assert.Equal("external", store.Files[ActivePath]);
        Assert.True(editor.IsDirty);
        Assert.Empty(activation.Configurations);
        Assert.Equal(
            ConfigurationWritePreconditionKind.MustBeMissing,
            store.LastWriteRequest!.Precondition.Kind);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("TargetAlreadyExists", StringComparison.Ordinal));
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task MalformedDraftCannotWriteOrReplaceTheActiveConfiguration()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        string previous = store.Files[ActivePath];

        editor.ConfigurationText = "{ nope";
        await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Invalid, editor.LoadState);
        Assert.True(editor.IsDirty);
        Assert.Equal(previous, store.Files[ActivePath]);
        Assert.Equal(0, store.WriteCount);
        Assert.Single(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task SaveFailureRetainsDirtyDraftAndExistingActivation()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        store.WriteResult = ConfigurationTextWriteResult.Failed(
            [
                new ConfigurationStorageProblem(
                    ConfigurationStorageProblemKind.PublicationFailure,
                    "Injected publication failure."),
            ]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        string previous = store.Files[ActivePath];
        editor.ConfigurationText = " \r\n" + previous;

        await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.StorageFailure, editor.LoadState);
        Assert.True(editor.IsDirty);
        Assert.Equal(previous, store.Files[ActivePath]);
        Assert.Equal(1, store.WriteCount);
        Assert.Single(activation.Configurations);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("PublicationFailure", StringComparison.Ordinal));
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulSaveCanonicalizesThenActivatesThePublishedConfiguration()
    {
        List<string> sequence = [];
        ScriptedTextStore store = StoreWithActiveConfiguration();
        store.WriteObserver = (_, _) => sequence.Add("write");
        RecordingActivation activation = new(_ => sequence.Add("activate"));
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        sequence.Clear();
        editor.ConfigurationText = " \r\n" + store.Files[ActivePath];

        await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["write", "activate"], sequence);
        Assert.Equal(1, store.WriteCount);
        Assert.Equal(2, activation.Configurations.Count);
        Assert.False(editor.IsDirty);
        Assert.Equal(store.Files[ActivePath], editor.ConfigurationText);
        Assert.Equal(ConfigurationLoadState.Loaded, editor.LoadState);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task SaveRejectsAStaleDraftAfterExternalContentChange()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        string loaded = store.Files[ActivePath];
        editor.ConfigurationText = " \r\n" + loaded;
        store.Files[ActivePath] = EmptyConfigurationJson();

        await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        Assert.True(editor.IsDirty);
        Assert.Equal(EmptyConfigurationJson(), store.Files[ActivePath]);
        Assert.Single(activation.Configurations);
        Assert.Equal(
            ConfigurationWritePreconditionKind.MustMatchRevision,
            store.LastWriteRequest!.Precondition.Kind);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("ChangedExternally", StringComparison.Ordinal));

        await editor.RevertAsync(TestContext.Current.CancellationToken);
        Assert.False(editor.IsDirty);
        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task InvalidImportDoesNotReplaceTheDocumentOrRuntime()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        store.Files[ImportPath] = "{ broken";
        QueuePathPicker picker = new(openPaths: [ImportPath]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            picker,
            activation);
        await editor.InitializeAsync();
        string previous = store.Files[ActivePath];

        await editor.ImportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Invalid, editor.LoadState);
        Assert.Equal(previous, store.Files[ActivePath]);
        Assert.Equal(previous, editor.ConfigurationText);
        Assert.Equal(0, store.WriteCount);
        Assert.Single(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task ValidImportReplacesWithoutMergingAndReactivates()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        store.Files[ImportPath] = EmptyConfigurationJson();
        QueuePathPicker picker = new(openPaths: [ImportPath]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            picker,
            activation);
        await editor.InitializeAsync();

        await editor.ImportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.WriteCount);
        Assert.Equal(2, activation.Configurations.Count);
        Assert.Empty(activation.Configurations[1].Rules);
        Assert.Equal(store.Files[ActivePath], editor.ConfigurationText);
        Assert.False(editor.IsDirty);
        Assert.Equal(ConfigurationLoadState.Loaded, editor.LoadState);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task ImportRejectsExternalChangesToTheLoadedActiveDocument()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        store.Files[ImportPath] = EmptyConfigurationJson();
        QueuePathPicker picker = new(openPaths: [ImportPath]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            picker,
            activation);
        await editor.InitializeAsync();
        string external = "external content";
        store.Files[ActivePath] = external;

        await editor.ImportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        Assert.Equal(external, store.Files[ActivePath]);
        Assert.Single(activation.Configurations);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("ChangedExternally", StringComparison.Ordinal));
        await editor.ValidateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task ExportWritesOnlyTheChosenDestinationAndKeepsDirtyActiveDraft()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        QueuePathPicker picker = new(savePaths: [ExportPath]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            picker,
            activation);
        await editor.InitializeAsync();
        string active = store.Files[ActivePath];
        editor.ConfigurationText = " \r\n" + active;

        await editor.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.WriteCount);
        Assert.Equal(active, store.Files[ActivePath]);
        Assert.True(store.Files.ContainsKey(ExportPath));
        Assert.Contains(ActivePath, store.LastWriteRequest!.ProtectedPaths);
        Assert.Equal(ActivePath, editor.ConfigurationPath);
        Assert.Equal(ActivePath, editor.ActiveConfigurationPath);
        Assert.True(editor.IsDirty);
        Assert.Single(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task PhysicalExportAliasConflictLeavesTheActiveDocumentUntouched()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        store.WriteResult = ConfigurationTextWriteResult.Conflicted(
            new ConfigurationWriteConflict(
                ConfigurationWriteConflictKind.ProtectedPathAlias,
                "Injected physical alias."));
        QueuePathPicker picker = new(savePaths: [ExportPath]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            picker,
            activation);
        await editor.InitializeAsync();
        string active = store.Files[ActivePath];

        await editor.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        Assert.Equal(active, store.Files[ActivePath]);
        Assert.False(store.Files.ContainsKey(ExportPath));
        Assert.Single(activation.Configurations);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("ProtectedPathAlias", StringComparison.Ordinal));
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task RuntimeActivationFailureRemainsVisibleAcrossValidateAndRevert()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        RecordingActivation activation = new()
        {
            Failure = new InvalidOperationException("Injected activation failure."),
        };
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);

        await editor.InitializeAsync();
        Assert.Equal(ConfigurationLoadState.RuntimeFailure, editor.LoadState);
        Assert.Equal("No active configuration", editor.ActiveConfigurationPath);

        await editor.ValidateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConfigurationLoadState.RuntimeFailure, editor.LoadState);

        editor.ConfigurationText = " \r\n" + store.Files[ActivePath];
        await editor.RevertAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConfigurationLoadState.RuntimeFailure, editor.LoadState);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("Injected activation failure", StringComparison.Ordinal));
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulExportDoesNotHideAnExistingRuntimeFailure()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        RecordingActivation activation = new()
        {
            Failure = new InvalidOperationException("Injected activation failure."),
        };
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(savePaths: [ExportPath]),
            activation);
        await editor.InitializeAsync();

        await editor.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.RuntimeFailure, editor.LoadState);
        Assert.True(store.Files.ContainsKey(ExportPath));
        Assert.Contains("exported", editor.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("Injected activation failure", StringComparison.Ordinal));
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulExportDoesNotHideAnExistingDocumentConflict()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(savePaths: [ExportPath]),
            new RecordingActivation());
        await editor.InitializeAsync();
        editor.ConfigurationText = " \r\n" + store.Files[ActivePath];
        store.Files[ActivePath] = EmptyConfigurationJson();
        await editor.SaveAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);

        await editor.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Conflict, editor.LoadState);
        Assert.True(store.Files.ContainsKey(ExportPath));
        Assert.Contains("exported", editor.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(editor.Problems, static problem =>
            problem.Contains("ChangedExternally", StringComparison.Ordinal));
        Assert.True(editor.IsDirty);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task FailedReplacementActivationKeepsThePreviousActivePathVisible()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        activation.Failure = new InvalidOperationException("Injected replacement failure.");
        editor.ConfigurationText = EmptyConfigurationJson();

        await editor.SaveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.RuntimeFailure, editor.LoadState);
        Assert.Equal(ActivePath, editor.ActiveConfigurationPath);
        Assert.False(editor.IsDirty);
        Assert.Single(activation.Configurations);
        Assert.Empty(OrganizationConfigurationJsonCodec.Decode(store.Files[ActivePath])
            .Configuration!.Rules);
        await editor.ValidateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConfigurationLoadState.RuntimeFailure, editor.LoadState);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task ExportToActiveConfigurationIsRejectedWithoutWriting()
    {
        ScriptedTextStore store = StoreWithActiveConfiguration();
        QueuePathPicker picker = new(savePaths: [ActivePath]);
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            picker,
            activation);
        await editor.InitializeAsync();
        string active = store.Files[ActivePath];
        editor.ConfigurationText = " \r\n" + active;

        await editor.ExportAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConfigurationLoadState.Invalid, editor.LoadState);
        Assert.Equal(0, store.WriteCount);
        Assert.Equal(active, store.Files[ActivePath]);
        Assert.True(editor.IsDirty);
        Assert.Contains("different", editor.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Single(activation.Configurations);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task OverlappingSaveRequestsPublishOnlyOnce()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TaskCompletionSource writeEntered = CompletionSource();
        TaskCompletionSource writeRelease = CompletionSource();
        ScriptedTextStore store = StoreWithActiveConfiguration();
        store.WriteAsyncOverride = async (request, token) =>
        {
            writeEntered.TrySetResult();
            await writeRelease.Task.WaitAsync(token);
            store.Files[ActivePath] = request.Content;
            return ConfigurationTextWriteResult.Succeeded(Revision(request.Content));
        };
        RecordingActivation activation = new();
        ConfigurationEditorViewModel editor = CreateEditor(
            Arguments(ActivePath),
            store,
            new QueuePathPicker(),
            activation);
        await editor.InitializeAsync();
        editor.ConfigurationText = " \r\n" + store.Files[ActivePath];

        Task first = editor.SaveAsync(cancellationToken);
        await writeEntered.Task.WaitAsync(cancellationToken);
        Task overlapping = editor.SaveAsync(cancellationToken);
        await overlapping;
        Assert.Equal(1, store.WriteCount);

        writeRelease.TrySetResult();
        await first;

        Assert.Equal(1, store.WriteCount);
        Assert.Equal(2, activation.Configurations.Count);
        await editor.DisposeAsync();
    }

    [Fact]
    public async Task DisposalCancelsAndJoinsAnOpenPickerOperation()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        TaskCompletionSource pickerEntered = CompletionSource();
        QueuePathPicker picker = new()
        {
            OpenOverride = async token =>
            {
                pickerEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            },
        };
        ConfigurationEditorViewModel editor = CreateEditor(
            ConfigurationPathArguments.Parse([]),
            new ScriptedTextStore(),
            picker,
            new RecordingActivation());
        await editor.InitializeAsync();

        Task open = editor.OpenAsync(cancellationToken);
        await pickerEntered.Task.WaitAsync(cancellationToken);
        Task disposal = editor.DisposeAsync().AsTask();

        await disposal.WaitAsync(cancellationToken);
        await open.WaitAsync(cancellationToken);
        Assert.False(editor.IsBusy);
    }

    private static ConfigurationEditorViewModel CreateEditor(
        ConfigurationPathArguments arguments,
        ScriptedTextStore store,
        QueuePathPicker picker,
        RecordingActivation activation) =>
        new(
            arguments,
            new OrganizationConfigurationService(store),
            picker,
            activation.ActivateAsync);

    private static ConfigurationPathArguments Arguments(string path) =>
        ConfigurationPathArguments.Parse(["--config", path]);

    private static ScriptedTextStore StoreWithActiveConfiguration()
    {
        ScriptedTextStore store = new();
        store.Files[ActivePath] = ConfigurationJson();
        return store;
    }

    private static string ConfigurationJson() =>
        """
        {
          "schemaVersion": 1,
          "pathPolicy": {
            "syntax": "windows",
            "caseSensitivity": "insensitive",
            "unicodeNormalization": "formC"
          },
          "watchRoots": [],
          "rules": [
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "Text files",
              "enabled": true,
              "order": 0,
              "include": {
                "type": "extension",
                "extensions": [".txt"],
                "caseSensitivity": "insensitive"
              },
              "exclude": null,
              "action": {
                "type": "moveToDirectory",
                "destinationDirectory": "C:\\Archive"
              }
            }
          ]
        }
        """;

    private static string EmptyConfigurationJson() =>
        """
        {
          "schemaVersion": 1,
          "pathPolicy": {
            "syntax": "windows",
            "caseSensitivity": "insensitive",
            "unicodeNormalization": "formC"
          },
          "watchRoots": [],
          "rules": []
        }
        """;

    private static TaskCompletionSource CompletionSource() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private static ConfigurationDocumentRevision Revision(string content) =>
        ConfigurationDocumentRevision.FromBytes(Encoding.UTF8.GetBytes(content));

    private sealed class RecordingActivation(Action<OrganizationConfiguration>? observer = null)
    {
        public List<OrganizationConfiguration> Configurations { get; } = [];

        public Exception? Failure { get; set; }

        public Task ActivateAsync(
            OrganizationConfiguration configuration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            Configurations.Add(configuration);
            observer?.Invoke(configuration);
            return Task.CompletedTask;
        }
    }

    private sealed class QueuePathPicker(
        IEnumerable<string>? openPaths = null,
        IEnumerable<string>? savePaths = null) : IConfigurationPathPicker
    {
        private readonly Queue<string> _openPaths = new(openPaths ?? []);
        private readonly Queue<string> _savePaths = new(savePaths ?? []);

        public Func<CancellationToken, ValueTask<string?>>? OpenOverride { get; init; }

        public ValueTask<string?> OpenExistingAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return OpenOverride is not null
                ? OpenOverride(cancellationToken)
                : ValueTask.FromResult<string?>(
                    _openPaths.TryDequeue(out string? path) ? path : null);
        }

        public ValueTask<string?> ChooseSaveDestinationAsync(
            string suggestedFileName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(
                _savePaths.TryDequeue(out string? path) ? path : null);
        }
    }

    private sealed class ScriptedTextStore : IConfigurationTextStore
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public int ReadCount { get; private set; }

        public int WriteCount { get; private set; }

        public ConfigurationTextWriteResult? WriteResult { get; set; }

        public Action<string, string>? WriteObserver { get; set; }

        public Func<ConfigurationTextWriteRequest, CancellationToken,
            ValueTask<ConfigurationTextWriteResult>>?
            WriteAsyncOverride { get; set; }

        public ConfigurationTextWriteRequest? LastWriteRequest { get; private set; }

        public Func<string, CancellationToken, ValueTask<ConfigurationTextReadResult>>?
            ReadAsyncOverride { get; set; }

        public ValueTask<ConfigurationTextReadResult> ReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ReadAsyncOverride is not null
                ? ReadAsyncOverride(path, cancellationToken)
                : ValueTask.FromResult(
                    Files.TryGetValue(path, out string? content)
                        ? ConfigurationTextReadResult.Found(content, Revision(content))
                        : ConfigurationTextReadResult.NotFound());
        }

        public async ValueTask<ConfigurationTextWriteResult> WriteAsync(
            ConfigurationTextWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            LastWriteRequest = request;
            WriteObserver?.Invoke(request.Path, request.Content);
            ConfigurationTextWriteResult result = WriteAsyncOverride is null
                ? WriteResult ?? EvaluateWrite(request)
                : await WriteAsyncOverride(request, cancellationToken);
            if (result.Status == ConfigurationTextWriteStatus.Succeeded
                && WriteAsyncOverride is null)
            {
                Files[request.Path] = request.Content;
            }

            return result;
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

            bool protectedAlias = request.ProtectedPaths.Any(protectedPath =>
                string.Equals(
                    Path.GetFullPath(request.Path),
                    Path.GetFullPath(protectedPath),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal));
            if (protectedAlias)
            {
                return Conflict(ConfigurationWriteConflictKind.ProtectedPathAlias);
            }

            return ConfigurationTextWriteResult.Succeeded(Revision(request.Content));
        }

        private static ConfigurationTextWriteResult Conflict(
            ConfigurationWriteConflictKind kind) =>
            ConfigurationTextWriteResult.Conflicted(
                new ConfigurationWriteConflict(kind, "Injected conflict."));
    }
}
