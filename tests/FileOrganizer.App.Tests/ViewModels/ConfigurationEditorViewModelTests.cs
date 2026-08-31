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
        Assert.Equal(ActivePath, editor.ConfigurationPath);
        Assert.Equal(ActivePath, editor.ActiveConfigurationPath);
        Assert.True(editor.IsDirty);
        Assert.Single(activation.Configurations);
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
        store.WriteAsyncOverride = async (_, content, token) =>
        {
            writeEntered.TrySetResult();
            await writeRelease.Task.WaitAsync(token);
            store.Files[ActivePath] = content;
            return ConfigurationTextWriteResult.Succeeded();
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

    private sealed class RecordingActivation(Action<OrganizationConfiguration>? observer = null)
    {
        public List<OrganizationConfiguration> Configurations { get; } = [];

        public Task ActivateAsync(
            OrganizationConfiguration configuration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

        public ConfigurationTextWriteResult WriteResult { get; set; } =
            ConfigurationTextWriteResult.Succeeded();

        public Action<string, string>? WriteObserver { get; set; }

        public Func<string, string, CancellationToken, ValueTask<ConfigurationTextWriteResult>>?
            WriteAsyncOverride { get; set; }

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
                        ? ConfigurationTextReadResult.Found(content)
                        : ConfigurationTextReadResult.NotFound());
        }

        public async ValueTask<ConfigurationTextWriteResult> WriteAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            WriteObserver?.Invoke(path, content);
            ConfigurationTextWriteResult result = WriteAsyncOverride is null
                ? WriteResult
                : await WriteAsyncOverride(path, content, cancellationToken);
            if (result.Status == ConfigurationTextWriteStatus.Succeeded
                && WriteAsyncOverride is null)
            {
                Files[path] = content;
            }

            return result;
        }
    }
}
