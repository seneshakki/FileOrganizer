using System.Collections.ObjectModel;
using System.Diagnostics;
using FileOrganizer.App.Services;
using FileOrganizer.App.Startup;
using FileOrganizer.Application.Configuration;
using FileOrganizer.Core.Paths;

namespace FileOrganizer.App.ViewModels;

/// <summary>
/// Owns one editable configuration document and publishes only validated immutable snapshots.
/// </summary>
public sealed class ConfigurationEditorViewModel : ViewModelBase, IAsyncDisposable
{
    private const string SuggestedConfigurationFileName = "fileorganizer.json";

    private readonly object _gate = new();
    private readonly ConfigurationPathArguments _arguments;
    private readonly OrganizationConfigurationService _configurationService;
    private readonly IConfigurationPathPicker _pathPicker;
    private readonly Func<OrganizationConfiguration, CancellationToken, Task> _activateConfiguration;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ReadOnlyCollection<string> _emptyProblems =
        Array.AsReadOnly(Array.Empty<string>());

    private ReadOnlyCollection<string> _problems;
    private ReadOnlyCollection<string> _persistedProblems;
    private ConfigurationLoadState _loadState = ConfigurationLoadState.AwaitingPath;
    private ConfigurationLoadState _persistedState = ConfigurationLoadState.AwaitingPath;
    private string _status = "Choose or create a configuration file to begin.";
    private string _persistedStatus = "Choose or create a configuration file to begin.";
    private string? _runtimeFailureStatus;
    private ReadOnlyCollection<string>? _runtimeFailureProblems;
    private string? _documentConflictStatus;
    private ReadOnlyCollection<string>? _documentConflictProblems;
    private string? _documentPath;
    private string? _activeConfigurationPath;
    private string _documentText = string.Empty;
    private string? _persistedText;
    private ConfigurationDocumentRevision? _persistedRevision;
    private bool _hasDocument;
    private bool _isDirty;
    private bool _isBusy;
    private bool _disposed;
    private Task? _initialization;
    private Task? _activeOperation;
    private Task? _disposal;

    public ConfigurationEditorViewModel(
        ConfigurationPathArguments arguments,
        OrganizationConfigurationService configurationService,
        IConfigurationPathPicker pathPicker,
        Func<OrganizationConfiguration, CancellationToken, Task> activateConfiguration)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(pathPicker);
        ArgumentNullException.ThrowIfNull(activateConfiguration);

        _arguments = arguments;
        _configurationService = configurationService;
        _pathPicker = pathPicker;
        _activateConfiguration = activateConfiguration;
        _problems = _emptyProblems;
        _persistedProblems = _emptyProblems;

        OpenCommand = BuildCommand(OpenAsync, CanOpen);
        CreateCommand = BuildCommand(CreateAsync, CanCreate);
        ReloadCommand = BuildCommand(ReloadAsync, CanReload);
        ValidateCommand = BuildCommand(ValidateAsync, CanValidate);
        SaveCommand = BuildCommand(SaveAsync, CanSave);
        ImportCommand = BuildCommand(ImportAsync, CanImport);
        ExportCommand = BuildCommand(ExportAsync, CanExport);
        RevertCommand = BuildCommand(RevertAsync, CanRevert);
    }

    public ConfigurationLoadState LoadState => _loadState;

    public string Status => _status;

    public IReadOnlyList<string> Problems => _problems;

    public bool HasProblems => _problems.Count != 0;

    public string ConfigurationPath => _documentPath ?? "No configuration selected";

    public string ActiveConfigurationPath =>
        _activeConfigurationPath ?? "No active configuration";

    public string ConfigurationText
    {
        get => _documentText;
        set => UpdateDraft(value ?? string.Empty);
    }

    public bool HasDocument => _hasDocument;

    public bool IsDirty => _isDirty;

    public bool IsBusy => _isBusy;

    public AsyncDelegateCommand OpenCommand { get; }

    public AsyncDelegateCommand CreateCommand { get; }

    public AsyncDelegateCommand ReloadCommand { get; }

    public AsyncDelegateCommand ValidateCommand { get; }

    public AsyncDelegateCommand SaveCommand { get; }

    public AsyncDelegateCommand ImportCommand { get; }

    public AsyncDelegateCommand ExportCommand { get; }

    public AsyncDelegateCommand RevertCommand { get; }

    public Task InitializeAsync()
    {
        TaskCompletionSource? start = null;
        Task initialization;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialization is null)
            {
                start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _initialization = start.Task;
            }

            initialization = _initialization;
        }

        if (start is not null)
        {
            _ = CompleteInitializationAsync(start);
        }

        return initialization;
    }

    public Task OpenAsync(CancellationToken cancellationToken = default) =>
        CanOpen()
            ? RunExclusiveAsync(OpenCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public Task CreateAsync(CancellationToken cancellationToken = default) =>
        CanCreate()
            ? RunExclusiveAsync(CreateCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public Task ReloadAsync(CancellationToken cancellationToken = default) =>
        CanReload()
            ? RunExclusiveAsync(ReloadCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public Task ValidateAsync(CancellationToken cancellationToken = default) =>
        CanValidate()
            ? RunExclusiveAsync(ValidateCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public Task SaveAsync(CancellationToken cancellationToken = default) =>
        CanSave()
            ? RunExclusiveAsync(SaveCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public Task ImportAsync(CancellationToken cancellationToken = default) =>
        CanImport()
            ? RunExclusiveAsync(ImportCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public Task ExportAsync(CancellationToken cancellationToken = default) =>
        CanExport()
            ? RunExclusiveAsync(ExportCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public Task RevertAsync(CancellationToken cancellationToken = default) =>
        CanRevert()
            ? RunExclusiveAsync(RevertCoreAsync, cancellationToken)
            : Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Task disposal;
        lock (_gate)
        {
            _disposal ??= DisposeCoreAsync();
            disposal = _disposal;
        }

        return new ValueTask(disposal);
    }

    private AsyncDelegateCommand BuildCommand(
        Func<CancellationToken, Task> execute,
        Func<bool> canExecute) =>
        new(execute, ReportCommandFailure, canExecute);

    private async Task CompleteInitializationAsync(TaskCompletionSource completion)
    {
        try
        {
            await Task.Yield();
            await RunExclusiveAsync(InitializeCoreAsync, _lifetime.Token);
            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            try
            {
                SetUnexpectedFailure(exception);
            }
            catch (Exception presentationFailure)
            {
                Trace.TraceError(
                    "FileOrganizer initialization failure presentation failed: {0}",
                    new AggregateException(exception, presentationFailure));
            }

            completion.TrySetException(exception);
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (!_arguments.IsValid)
        {
            SetProblems(
                ConfigurationLoadState.AwaitingPath,
                "Startup arguments were not accepted. No file was read or created.",
                [_arguments.Problem!]);
            return;
        }

        if (!_arguments.HasConfigurationPath)
        {
            SetPresentation(
                ConfigurationLoadState.AwaitingPath,
                "Choose or create a configuration file to begin.",
                []);
            return;
        }

        await LoadPathCoreAsync(_arguments.ConfigurationPath!, cancellationToken);
    }

    private async Task OpenCoreAsync(CancellationToken cancellationToken)
    {
        string? path = await _pathPicker.OpenExistingAsync(cancellationToken);
        if (path is not null)
        {
            await LoadPathCoreAsync(path, cancellationToken);
        }
    }

    private async Task CreateCoreAsync(CancellationToken cancellationToken)
    {
        string? path = _documentPath is not null && !_hasDocument
            ? _documentPath
            : await _pathPicker.ChooseSaveDestinationAsync(
                SuggestedConfigurationFileName,
                cancellationToken);
        if (path is null)
        {
            return;
        }

        OrganizationConfiguration configuration = CreateEmptyWindowsConfiguration();
        await SaveAndActivateAsync(
            path,
            configuration,
            ConfigurationWritePrecondition.MustBeMissing,
            cancellationToken);
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken)
    {
        if (_documentPath is null)
        {
            return;
        }

        await LoadPathCoreAsync(_documentPath, cancellationToken);
    }

    private Task ValidateCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(
            _documentText);
        if (!decoded.Succeeded)
        {
            SetConfigurationProblems(
                ConfigurationLoadState.Invalid,
                "The draft is invalid and cannot be saved or activated.",
                decoded.Problems);
            return Task.CompletedTask;
        }

        SetPresentation(
            ConfigurationLoadState.Editing,
            "The draft is valid and has not been saved.",
            []);
        if (!_isDirty)
        {
            RestoreStablePresentation();
        }

        return Task.CompletedTask;
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        if (_documentPath is null)
        {
            return;
        }

        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(
            _documentText);
        if (!decoded.Succeeded)
        {
            SetConfigurationProblems(
                ConfigurationLoadState.Invalid,
                "The draft is invalid; nothing was written.",
                decoded.Problems);
            return;
        }

        await SaveAndActivateAsync(
            _documentPath,
            decoded.Configuration!,
            _persistedRevision is null
                ? ConfigurationWritePrecondition.MustBeMissing
                : ConfigurationWritePrecondition.MustMatch(_persistedRevision),
            cancellationToken);
    }

    private async Task ImportCoreAsync(CancellationToken cancellationToken)
    {
        string? sourcePath = await _pathPicker.OpenExistingAsync(cancellationToken);
        if (sourcePath is null)
        {
            return;
        }

        string? activePath = _documentPath ??
            await _pathPicker.ChooseSaveDestinationAsync(
                SuggestedConfigurationFileName,
                cancellationToken);
        if (activePath is null)
        {
            return;
        }

        SetPresentation(
            ConfigurationLoadState.Importing,
            "Validating and importing configuration…",
            []);
        ConfigurationImportResult import = await _configurationService.ImportAsync(
            sourcePath,
            activePath,
            _persistedRevision is null
                ? ConfigurationWritePrecondition.MustBeMissing
                : ConfigurationWritePrecondition.MustMatch(_persistedRevision),
            cancellationToken);
        switch (import.Status)
        {
            case ConfigurationImportStatus.SourceNotFound:
                SetProblems(
                    ConfigurationLoadState.Missing,
                    "The import source was not found; the active configuration was not changed.",
                    ["The selected import source does not exist."]);
                return;
            case ConfigurationImportStatus.Invalid:
                SetConfigurationProblems(
                    ConfigurationLoadState.Invalid,
                    "The import is invalid; the active configuration was not changed.",
                    import.ConfigurationProblems);
                return;
            case ConfigurationImportStatus.Conflict:
                SetDocumentConflict(
                    "The active configuration changed externally; the import was not published.",
                    import.Conflict!);
                return;
            case ConfigurationImportStatus.StorageFailure:
                SetStorageProblems(
                    "The import could not be published; the active configuration was not changed.",
                    import.StorageProblems);
                return;
            case ConfigurationImportStatus.Imported:
                ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(
                    import.Configuration!);
                if (!encoded.Succeeded)
                {
                    SetConfigurationProblems(
                        ConfigurationLoadState.Invalid,
                        "The imported configuration could not be represented safely.",
                        encoded.Problems);
                    return;
                }

                SetDocument(
                    activePath,
                    encoded.Json!,
                    import.Revision!,
                    ConfigurationLoadState.Loaded,
                    "Configuration imported and saved.",
                    []);
                await ActivateAsync(
                    activePath,
                    import.Configuration!,
                    "Configuration imported, saved, and activated.",
                    cancellationToken);
                return;
            default:
                throw new InvalidOperationException("Configuration import returned an unknown status.");
        }
    }

    private async Task ExportCoreAsync(CancellationToken cancellationToken)
    {
        ConfigurationDecodeResult decoded = OrganizationConfigurationJsonCodec.Decode(
            _documentText);
        if (!decoded.Succeeded)
        {
            SetConfigurationProblems(
                ConfigurationLoadState.Invalid,
                "The draft is invalid; nothing was exported.",
                decoded.Problems);
            return;
        }

        string? destinationPath = await _pathPicker.ChooseSaveDestinationAsync(
            SuggestedConfigurationFileName,
            cancellationToken);
        if (destinationPath is null)
        {
            return;
        }

        if (IsSamePath(destinationPath, _documentPath)
            || IsSamePath(destinationPath, _activeConfigurationPath))
        {
            SetProblems(
                ConfigurationLoadState.Invalid,
                "The export destination must be different from the current document and active configuration.",
                ["Use Save to update the current document, or choose a different export file."]);
            return;
        }

        SetPresentation(
            ConfigurationLoadState.Exporting,
            "Publishing an independent configuration export…",
            []);
        ConfigurationSaveResult export = await _configurationService.ExportAsync(
            destinationPath,
            decoded.Configuration!,
            new[] { _documentPath, _activeConfigurationPath }
                .Where(static path => path is not null)
                .Select(static path => path!)
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal),
            cancellationToken);
        if (export.Status == ConfigurationSaveStatus.Invalid)
        {
            SetConfigurationProblems(
                ConfigurationLoadState.Invalid,
                "The draft is invalid; nothing was exported.",
                export.ConfigurationProblems);
            return;
        }

        if (export.Status == ConfigurationSaveStatus.StorageFailure)
        {
            SetStorageProblems(
                "The export could not be published. The active configuration was not changed.",
                export.StorageProblems);
            return;
        }

        if (export.Status == ConfigurationSaveStatus.Conflict)
        {
            SetConflict(
                "The export destination conflicts with the current or active configuration; nothing was exported.",
                export.Conflict!);
            return;
        }

        if (_documentConflictStatus is not null && _documentConflictProblems is not null)
        {
            SetPresentation(
                ConfigurationLoadState.Conflict,
                $"Configuration exported to {destinationPath}. {_documentConflictStatus}",
                _documentConflictProblems);
        }
        else if (_runtimeFailureStatus is not null && _runtimeFailureProblems is not null)
        {
            SetPresentation(
                ConfigurationLoadState.RuntimeFailure,
                $"Configuration exported to {destinationPath}. {_runtimeFailureStatus}",
                _runtimeFailureProblems);
        }
        else
        {
            SetPresentation(
                _isDirty ? ConfigurationLoadState.Editing : _persistedState,
                $"Configuration exported to {destinationPath}. The active configuration was not changed.",
                []);
        }
    }

    private Task RevertCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_persistedText is null)
        {
            return Task.CompletedTask;
        }

        _documentText = _persistedText;
        _isDirty = false;
        RestoreStablePresentation();
        RaiseDocumentProperties();
        return Task.CompletedTask;
    }

    private async Task LoadPathCoreAsync(string path, CancellationToken cancellationToken)
    {
        SetPresentation(
            ConfigurationLoadState.Loading,
            "Loading and validating configuration…",
            []);
        ConfigurationLoadResult load = await _configurationService.LoadAsync(path, cancellationToken);
        switch (load.Status)
        {
            case ConfigurationLoadStatus.NotFound:
                SetDocument(
                    path,
                    string.Empty,
                    null,
                    ConfigurationLoadState.Missing,
                    "Configuration file was not found. Nothing was created.",
                    ["Use Create to publish an empty schema-v1 configuration at this path."],
                    hasDocument: false);
                return;
            case ConfigurationLoadStatus.Invalid:
                SetDocument(
                    path,
                    load.SourceText!,
                    load.Revision!,
                    ConfigurationLoadState.Invalid,
                    "Configuration is invalid; the active preview was not changed.",
                    FormatConfigurationProblems(load.ConfigurationProblems));
                return;
            case ConfigurationLoadStatus.StorageFailure:
                SetStorageProblems(
                    _documentPath is null
                        ? $"Configuration at {path} could not be read; no document was opened."
                        : $"Configuration at {path} could not be read. The current document {_documentPath} and active preview were not changed.",
                    load.StorageProblems);
                return;
            case ConfigurationLoadStatus.Loaded:
                SetDocument(
                    path,
                    load.SourceText!,
                    load.Revision!,
                    ConfigurationLoadState.Loaded,
                    "Configuration loaded and validated.",
                    []);
                await ActivateAsync(
                    path,
                    load.Configuration!,
                    load.Configuration!.WatchRoots.Count == 0
                        ? "Configuration activated. No watch roots are configured."
                        : $"Configuration activated. Monitoring {load.Configuration.WatchRoots.Count} configured root(s).",
                    cancellationToken);
                return;
            default:
                throw new InvalidOperationException("Configuration load returned an unknown status.");
        }
    }

    private async Task SaveAndActivateAsync(
        string path,
        OrganizationConfiguration configuration,
        ConfigurationWritePrecondition precondition,
        CancellationToken cancellationToken)
    {
        SetPresentation(
            ConfigurationLoadState.Saving,
            "Validating and publishing configuration…",
            []);
        ConfigurationSaveResult save = await _configurationService.SaveAsync(
            path,
            configuration,
            precondition,
            cancellationToken);
        if (save.Status == ConfigurationSaveStatus.Invalid)
        {
            SetConfigurationProblems(
                ConfigurationLoadState.Invalid,
                "The configuration is invalid; nothing was written.",
                save.ConfigurationProblems);
            return;
        }

        if (save.Status == ConfigurationSaveStatus.StorageFailure)
        {
            SetStorageProblems(
                "The configuration could not be published. The active preview was not changed.",
                save.StorageProblems);
            return;
        }

        if (save.Status == ConfigurationSaveStatus.Conflict)
        {
            SetDocumentConflict(
                precondition.Kind == ConfigurationWritePreconditionKind.MustBeMissing
                    ? "The configuration destination now exists; nothing was replaced or activated."
                    : "The configuration changed externally; the draft was not published or activated.",
                save.Conflict!);
            return;
        }

        ConfigurationEncodeResult encoded = OrganizationConfigurationJsonCodec.Encode(configuration);
        if (!encoded.Succeeded)
        {
            throw new InvalidOperationException(
                "A configuration accepted for saving could not be encoded after publication.");
        }

        SetDocument(
            path,
            encoded.Json!,
            save.Revision!,
            ConfigurationLoadState.Loaded,
            "Configuration saved.",
            []);
        await ActivateAsync(
            path,
            configuration,
            configuration.WatchRoots.Count == 0
                ? "Configuration saved and activated. No watch roots are configured."
                : $"Configuration saved and activated. Monitoring {configuration.WatchRoots.Count} configured root(s).",
            cancellationToken);
    }

    private async Task ActivateAsync(
        string path,
        OrganizationConfiguration configuration,
        string successStatus,
        CancellationToken cancellationToken)
    {
        try
        {
            await _activateConfiguration(configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetRuntimeFailure(
                "The configuration is valid and persisted, but the preview runtime could not be activated.",
                [exception.Message]);
            return;
        }

        _runtimeFailureStatus = null;
        _runtimeFailureProblems = null;
        _documentConflictStatus = null;
        _documentConflictProblems = null;
        _activeConfigurationPath = path;
        RaisePropertyChanged(nameof(ActiveConfigurationPath));
        SetPresentation(ConfigurationLoadState.Loaded, successStatus, []);
    }

    private Task RunExclusiveAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        TaskCompletionSource completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeOperation is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeOperation = completion.Task;
        }

        _ = CompleteOperationAsync(operation, completion, cancellationToken);
        return completion.Task;
    }

    private async Task CompleteOperationAsync(
        Func<CancellationToken, Task> operation,
        TaskCompletionSource completion,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            SetBusy(true);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token,
                cancellationToken);
            await operation(linked.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                SetBusy(false);
            }
            catch (Exception exception)
            {
                failure = failure is null
                    ? exception
                    : new AggregateException(failure, exception);
            }

            lock (_gate)
            {
                if (ReferenceEquals(_activeOperation, completion.Task))
                {
                    _activeOperation = null;
                }
            }
        }

        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }
    }

    private void UpdateDraft(string value)
    {
        if (string.Equals(_documentText, value, StringComparison.Ordinal))
        {
            return;
        }

        _documentText = value;
        _hasDocument = true;
        _isDirty = _persistedText is null
            || !string.Equals(_documentText, _persistedText, StringComparison.Ordinal);
        if (_isDirty)
        {
            _loadState = ConfigurationLoadState.Editing;
            _status = "The configuration draft has unsaved changes.";
            _problems = _emptyProblems;
        }
        else
        {
            RestoreStablePresentation();
        }

        RaiseDocumentProperties();
        if (_isDirty)
        {
            RaisePresentationProperties();
            NotifyCommandStates();
        }
    }

    private void SetDocument(
        string path,
        string text,
        ConfigurationDocumentRevision? revision,
        ConfigurationLoadState persistedState,
        string persistedStatus,
        IEnumerable<string> persistedProblems,
        bool hasDocument = true)
    {
        if (hasDocument != (revision is not null))
        {
            throw new ArgumentException(
                "An existing configuration document requires a storage revision.",
                nameof(revision));
        }

        string[] problemSnapshot = [.. persistedProblems];
        _documentPath = path;
        _documentText = text;
        _persistedText = hasDocument ? text : null;
        _persistedRevision = revision;
        _runtimeFailureStatus = null;
        _runtimeFailureProblems = null;
        _documentConflictStatus = null;
        _documentConflictProblems = null;
        _hasDocument = hasDocument;
        _isDirty = false;
        _persistedState = persistedState;
        _persistedStatus = persistedStatus;
        _persistedProblems = Array.AsReadOnly(problemSnapshot);
        _loadState = persistedState;
        _status = persistedStatus;
        _problems = _persistedProblems;
        RaiseDocumentProperties();
        RaisePresentationProperties();
        NotifyCommandStates();
    }

    private void SetConfigurationProblems(
        ConfigurationLoadState state,
        string status,
        IEnumerable<ConfigurationProblem> problems) =>
        SetProblems(state, status, FormatConfigurationProblems(problems));

    private void SetStorageProblems(
        string status,
        IEnumerable<ConfigurationStorageProblem> problems) =>
        SetProblems(
            ConfigurationLoadState.StorageFailure,
            status,
            problems.Select(static problem => $"{problem.Kind}: {problem.Description}"));

    private void SetConflict(string status, ConfigurationWriteConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        SetProblems(
            ConfigurationLoadState.Conflict,
            status,
            [$"{conflict.Kind}: {conflict.Description}"]);
    }

    private void SetDocumentConflict(string status, ConfigurationWriteConflict conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        _documentConflictStatus = status;
        _documentConflictProblems = Array.AsReadOnly(
            new[] { $"{conflict.Kind}: {conflict.Description}" });
        SetPresentation(
            ConfigurationLoadState.Conflict,
            _documentConflictStatus,
            _documentConflictProblems);
    }

    private void SetRuntimeFailure(string status, IEnumerable<string> problems)
    {
        string[] snapshot = [.. problems];
        _runtimeFailureStatus = status;
        _runtimeFailureProblems = Array.AsReadOnly(
            snapshot.Length == 0 ? ["An unspecified runtime problem occurred."] : snapshot);
        RestoreStablePresentation();
    }

    private void RestoreStablePresentation()
    {
        if (_documentConflictStatus is not null && _documentConflictProblems is not null)
        {
            _loadState = ConfigurationLoadState.Conflict;
            _status = _documentConflictStatus;
            _problems = _documentConflictProblems;
        }
        else if (_runtimeFailureStatus is not null && _runtimeFailureProblems is not null)
        {
            _loadState = ConfigurationLoadState.RuntimeFailure;
            _status = _runtimeFailureStatus;
            _problems = _runtimeFailureProblems;
        }
        else
        {
            _loadState = _persistedState;
            _status = _persistedStatus;
            _problems = _persistedProblems;
        }

        RaisePresentationProperties();
        NotifyCommandStates();
    }

    private void SetProblems(
        ConfigurationLoadState state,
        string status,
        IEnumerable<string> problems)
    {
        string[] snapshot = [.. problems];
        SetPresentation(
            state,
            status,
            snapshot.Length == 0 ? ["An unspecified problem occurred."] : snapshot);
    }

    private void SetPresentation(
        ConfigurationLoadState state,
        string status,
        IEnumerable<string> problems)
    {
        _loadState = state;
        _status = status;
        _problems = Array.AsReadOnly([.. problems]);
        RaisePresentationProperties();
        NotifyCommandStates();
    }

    private void SetBusy(bool value)
    {
        if (_isBusy == value)
        {
            return;
        }

        _isBusy = value;
        RaisePropertyChanged(nameof(IsBusy));
        NotifyCommandStates();
    }

    private void RaiseDocumentProperties()
    {
        RaisePropertyChanged(nameof(ConfigurationPath));
        RaisePropertyChanged(nameof(ConfigurationText));
        RaisePropertyChanged(nameof(HasDocument));
        RaisePropertyChanged(nameof(IsDirty));
    }

    private void RaisePresentationProperties()
    {
        RaisePropertyChanged(nameof(LoadState));
        RaisePropertyChanged(nameof(Status));
        RaisePropertyChanged(nameof(Problems));
        RaisePropertyChanged(nameof(HasProblems));
    }

    private void NotifyCommandStates()
    {
        OpenCommand.RaiseCanExecuteChanged();
        CreateCommand.RaiseCanExecuteChanged();
        ReloadCommand.RaiseCanExecuteChanged();
        ValidateCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        ExportCommand.RaiseCanExecuteChanged();
        RevertCommand.RaiseCanExecuteChanged();
    }

    private bool CanOpen() => !_isBusy && !_isDirty;

    private bool CanCreate() => !_isBusy && !_isDirty;

    private bool CanReload() => !_isBusy && !_isDirty && _documentPath is not null;

    private bool CanValidate() => !_isBusy && _hasDocument;

    private bool CanSave() => !_isBusy && _isDirty && _hasDocument && _documentPath is not null;

    private bool CanImport() => !_isBusy && !_isDirty;

    private bool CanExport() => !_isBusy && _hasDocument;

    private bool CanRevert() => !_isBusy && _isDirty && _persistedText is not null;

    private void ReportCommandFailure(Exception exception)
    {
        if (exception is OperationCanceledException && _lifetime.IsCancellationRequested)
        {
            return;
        }

        SetUnexpectedFailure(exception);
    }

    private void SetUnexpectedFailure(Exception exception) =>
        SetProblems(
            ConfigurationLoadState.RuntimeFailure,
            "An unexpected configuration workspace failure occurred.",
            [exception.Message]);

    private async Task DisposeCoreAsync()
    {
        Task? initialization;
        Task? activeOperation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            initialization = _initialization;
            activeOperation = _activeOperation;
        }

        Exception? failure = null;
        if (activeOperation is not null)
        {
            failure = await ObserveAsync(activeOperation);
        }

        if (initialization is not null && !ReferenceEquals(initialization, activeOperation))
        {
            Exception? initializationFailure = await ObserveAsync(initialization);
            if (failure is null)
            {
                failure = initializationFailure;
            }
            else if (initializationFailure is not null
                     && !ReferenceEquals(failure, initializationFailure))
            {
                failure = new AggregateException(failure, initializationFailure);
            }
        }

        _lifetime.Dispose();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private static async Task<Exception?> ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static IEnumerable<string> FormatConfigurationProblems(
        IEnumerable<ConfigurationProblem> problems) =>
        problems.Select(static problem =>
            $"{problem.Location} · {problem.Kind}: {problem.Description}");

    private static bool IsSamePath(string candidate, string? existing) =>
        existing is not null
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(existing)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static OrganizationConfiguration CreateEmptyWindowsConfiguration() =>
        new(
            new PathSemantics(
                PathSyntax.Windows,
                PathCaseSensitivity.Insensitive,
                PathUnicodeNormalization.FormC),
            [],
            []);
}
